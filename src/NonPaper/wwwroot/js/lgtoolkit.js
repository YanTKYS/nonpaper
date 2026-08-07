/**
 * lgtoolkit v0.1.0
 *
 * 静的HTML向けの汎用JavaScriptライブラリ。
 * ファイル読込・保存、クリップボード、Canvas、画像、DOM補助、非同期の世代管理など、
 * 特定の画面に依存しない処理だけをまとめる。
 *
 * 方針:
 * - 公開するグローバルは window.LGToolkit のみ
 * - jQuery・npm・バンドラー・CDN・外部通信に依存しない
 * - 成功／失敗の通知UI（トースト等）は持たず、結果は戻り値・Promise・例外で返す
 * - DOMは引数で受け取る（特定のID・CSSクラスを前提にしない）
 */
(function (global) {
    'use strict';

    var VERSION = '0.1.0';

    // 同一ファイルを誤って2回読み込んでも、既に公開済みの実体を壊さない。
    // 別バージョンが先に読み込まれている場合も上書きせず、読込順の問題として通知するに留める。
    if (global.LGToolkit) {
        if (global.LGToolkit.version !== VERSION) {
            if (global.console && global.console.warn) {
                global.console.warn(
                    'LGToolkit ' + global.LGToolkit.version + ' is already loaded. ' +
                    'Skipped loading version ' + VERSION + '.'
                );
            }
        }
        return;
    }

    // =============================================
    // 共通定数・内部ヘルパー（外部へは公開しない）
    // =============================================

    /** エラーの種別。呼び出し元は error.code で分岐できる。 */
    var ERROR_CODES = {
        INVALID_ARGUMENT: 'INVALID_ARGUMENT',
        INVALID_FILE: 'INVALID_FILE',
        SIZE_LIMIT: 'SIZE_LIMIT',
        READ_ERROR: 'READ_ERROR',
        CANVAS_EXPORT_FAILED: 'CANVAS_EXPORT_FAILED',
        IMAGE_LOAD_FAILED: 'IMAGE_LOAD_FAILED'
    };

    var BYTES_PER_UNIT = 1024;
    var BYTE_UNITS = ['B', 'KB', 'MB', 'GB', 'TB'];
    var DEFAULT_BYTE_DECIMALS = 1;

    /**
     * Object URLの解放待ち時間（ミリ秒）。
     * click()直後に解放するとダウンロードが始まらないブラウザがあるため、
     * 発火を確認できる十分な時間だけ遅らせてから解放する。
     */
    var OBJECT_URL_REVOKE_DELAY_MS = 60000;

    var DEFAULT_DOWNLOAD_FILE_NAME = 'download';
    var DEFAULT_PNG_FILE_NAME = 'image.png';
    var DEFAULT_TEXT_ENCODING = 'UTF-8';
    var DEFAULT_MIME_FALLBACK_EXTENSION = 'bin';
    var DEFAULT_TAB_ACTIVE_CLASS = 'active';
    var DEFAULT_DROPDOWN_CLOSE_SELECTOR = 'button';
    var MIN_SCALED_SIZE_PX = 1;

    /** 画像MIMEタイプと保存名に使う拡張子の対応（JPEGは jpg へ正規化）。 */
    var IMAGE_EXTENSION_BY_MIME_TYPE = {
        'image/jpeg': 'jpg',
        'image/jpg': 'jpg',
        'image/png': 'png',
        'image/webp': 'webp',
        'image/gif': 'gif',
        'image/bmp': 'bmp',
        'image/x-icon': 'ico',
        'image/vnd.microsoft.icon': 'ico'
    };

    /**
     * codeを付けたErrorを作る。エラー本文は内部向けの英語で統一し、
     * 利用者向けの表示文言は呼び出し元（アプリ側）が組み立てる。
     */
    function createError(code, message) {
        var error = new Error(message);
        error.code = code;
        return error;
    }

    function isFiniteNumber(value) {
        return typeof value === 'number' && isFinite(value);
    }

    /** File/Blobとして読めるかどうか（File未対応環境でもBlobで判定できるようにする）。 */
    function isBlobLike(value) {
        return !!value &&
            typeof value.size === 'number' &&
            typeof value.slice === 'function';
    }

    function isElement(value) {
        return !!value && value.nodeType === 1;
    }

    /** Canvasとして扱えるか（getContext/toBlobを持つか）で判定する。 */
    function isCanvasLike(value) {
        return !!value && typeof value.toBlob === 'function' && typeof value.getContext === 'function';
    }

    /**
     * 要素・配列・NodeList・jQueryオブジェクト等を、素の要素配列へ揃える。
     * 要素以外は取り除く。
     */
    function toElementArray(target) {
        if (!target) return [];
        if (isElement(target)) return [target];
        if (typeof target.length !== 'number') return [];

        var elements = [];
        for (var i = 0; i < target.length; i++) {
            if (isElement(target[i])) elements.push(target[i]);
        }
        return elements;
    }

    /**
     * ファイル名からディレクトリ部分と、拡張子の区切り位置を求める。
     * パス区切り（/ と \）を含む文字列でも、最後の区切り以降だけを対象にする。
     * 先頭ドットのみのファイル名（.gitignore 等）は「拡張子なし」として扱う。
     * @returns {{name:string, baseStart:number, dotIndex:number}} dotIndexは-1なら拡張子なし
     */
    function splitFileName(fileName) {
        var name = fileName == null ? '' : String(fileName);
        var baseStart = Math.max(name.lastIndexOf('/'), name.lastIndexOf('\\')) + 1;
        var dotIndex = name.lastIndexOf('.');
        if (dotIndex <= baseStart) dotIndex = -1;
        return { name: name, baseStart: baseStart, dotIndex: dotIndex };
    }

    /**
     * FileReaderでの読込を共通化する。読込前にサイズ上限を判定するため、
     * 上限超過のファイルはメモリへ載せずにrejectする。
     * @param {File|Blob} file 対象ファイル
     * @param {string} readAs 'text' | 'dataUrl' | 'arrayBuffer'
     * @param {Object} [options] maxBytes / encoding
     * @returns {Promise<string|ArrayBuffer>}
     */
    function readFile(file, readAs, options) {
        var settings = options || {};

        return new Promise(function (resolve, reject) {
            if (!isBlobLike(file)) {
                reject(createError(ERROR_CODES.INVALID_FILE, 'A File or Blob is required.'));
                return;
            }

            var maxBytes = settings.maxBytes;
            if (maxBytes !== undefined && maxBytes !== null) {
                if (!isFiniteNumber(maxBytes) || maxBytes < 0) {
                    reject(createError(ERROR_CODES.INVALID_ARGUMENT, 'maxBytes must be a non-negative finite number.'));
                    return;
                }
                if (file.size > maxBytes) {
                    reject(createError(ERROR_CODES.SIZE_LIMIT, 'File size exceeds the limit.'));
                    return;
                }
            }

            var reader = new FileReader();
            // 読込失敗・中断はいずれも握りつぶさず、READ_ERRORとして呼び出し元へ返す
            reader.onerror = function () {
                reject(createError(ERROR_CODES.READ_ERROR, 'Failed to read the file.'));
            };
            reader.onabort = function () {
                reject(createError(ERROR_CODES.READ_ERROR, 'Reading the file was aborted.'));
            };
            reader.onload = function () {
                resolve(reader.result);
            };

            try {
                if (readAs === 'text') {
                    reader.readAsText(file, settings.encoding || DEFAULT_TEXT_ENCODING);
                } else if (readAs === 'dataUrl') {
                    reader.readAsDataURL(file);
                } else {
                    reader.readAsArrayBuffer(file);
                }
            } catch (e) {
                reject(createError(ERROR_CODES.READ_ERROR, 'Failed to start reading the file.'));
            }
        });
    }

    // =============================================
    // async: 非同期処理の世代管理
    // =============================================

    var asyncNamespace = {
        /**
         * 「最後に開始した要求の結果だけを有効にする」ための世代トークンを管理する。
         * 非同期処理は完了順が前後するため、古い要求の結果で新しい表示を上書きしないよう
         * 呼び出し元が isCurrent() で判定する。
         * トークンは単純な数値で、内部の現在値は外部へ公開しない。
         * @returns {{begin: function(): number, isCurrent: function(number): boolean, invalidate: function(): void}}
         */
        createRequestTracker: function () {
            var current = 0;

            return {
                /** 新しい要求を開始し、その世代トークンを返す。 */
                begin: function () {
                    current++;
                    return current;
                },

                /** 受け取ったトークンが最新かどうか。falseなら結果を反映しない。 */
                isCurrent: function (token) {
                    return token === current;
                },

                /** 進行中の要求をすべて無効化する（クリア・破棄時に使う）。 */
                invalidate: function () {
                    current++;
                }
            };
        }
    };

    // =============================================
    // files: ファイル読込・保存・ファイル名操作
    // =============================================

    var filesNamespace = {
        /**
         * Fileをテキストとして読み込む。
         * @param {File|Blob} file 対象ファイル
         * @param {Object} [options]
         * @param {number} [options.maxBytes] 上限バイト数（超過時は読込前にreject）
         * @param {string} [options.encoding] 文字エンコーディング（既定: UTF-8）
         * @returns {Promise<string>} INVALID_FILE / SIZE_LIMIT / READ_ERROR でreject
         */
        readAsText: function (file, options) {
            return readFile(file, 'text', options);
        },

        /**
         * FileをData URLとして読み込む。
         * @param {File|Blob} file 対象ファイル
         * @param {Object} [options]
         * @param {number} [options.maxBytes] 上限バイト数
         * @returns {Promise<string>} INVALID_FILE / SIZE_LIMIT / READ_ERROR でreject
         */
        readAsDataUrl: function (file, options) {
            return readFile(file, 'dataUrl', options);
        },

        /**
         * FileをArrayBufferとして読み込む。
         * @param {File|Blob} file 対象ファイル
         * @param {Object} [options]
         * @param {number} [options.maxBytes] 上限バイト数
         * @returns {Promise<ArrayBuffer>} INVALID_FILE / SIZE_LIMIT / READ_ERROR でreject
         */
        readAsArrayBuffer: function (file, options) {
            return readFile(file, 'arrayBuffer', options);
        },

        /**
         * 指定URLをダウンロードとして発火させる。
         * アンカーはDOMへ追加してからclick()する（未追加だと発火しないブラウザがある）。
         * URLの生成・解放は呼び出し元の責任とする。
         * @param {string} url data URL または Object URL
         * @param {string} [fileName] 保存名（空の場合は既定名）
         * @throws {Error} INVALID_ARGUMENT
         */
        downloadUrl: function (url, fileName) {
            if (typeof url !== 'string' || !url) {
                throw createError(ERROR_CODES.INVALID_ARGUMENT, 'url must be a non-empty string.');
            }

            var link = document.createElement('a');
            link.href = url;
            link.download = fileName ? String(fileName) : DEFAULT_DOWNLOAD_FILE_NAME;
            document.body.appendChild(link);
            try {
                link.click();
            } finally {
                // 例外時も一時DOMを残さない
                document.body.removeChild(link);
            }
        },

        /**
         * Blobをファイルとして保存する。Object URLの生成と解放まで面倒を見る。
         * @param {Blob} blob 保存するBlob
         * @param {string} [fileName] 保存名（空の場合は既定名）
         * @throws {Error} INVALID_ARGUMENT
         */
        downloadBlob: function (blob, fileName) {
            if (!isBlobLike(blob)) {
                throw createError(ERROR_CODES.INVALID_ARGUMENT, 'blob must be a Blob.');
            }

            var url = URL.createObjectURL(blob);
            try {
                filesNamespace.downloadUrl(url, fileName || DEFAULT_DOWNLOAD_FILE_NAME);
            } catch (e) {
                URL.revokeObjectURL(url);
                throw e;
            }
            setTimeout(function () {
                URL.revokeObjectURL(url);
            }, OBJECT_URL_REVOKE_DELAY_MS);
        },

        /**
         * ファイル名から拡張子（最後のドット以降）を取り除く。
         * ドットが無い場合と、先頭ドットのみのファイル名（.gitignore等）はそのまま返す。
         * パス区切りを含む場合は、最後の区切り以降のみを対象にする。
         * @param {string} fileName 元のファイル名
         * @returns {string} 拡張子を除いたファイル名
         */
        removeExtension: function (fileName) {
            var parts = splitFileName(fileName);
            if (parts.dotIndex < 0) return parts.name;
            return parts.name.slice(0, parts.dotIndex);
        },

        /**
         * ファイル名から拡張子を小文字で取り出す（ドットは含まない）。
         * 拡張子が無い場合と、先頭ドットのみのファイル名は空文字を返す。
         * @param {string} fileName 元のファイル名
         * @returns {string} 拡張子（ドットなし・小文字）
         */
        getExtension: function (fileName) {
            var parts = splitFileName(fileName);
            if (parts.dotIndex < 0) return '';
            return parts.name.slice(parts.dotIndex + 1).toLowerCase();
        },

        /**
         * バイト数を読みやすい単位付き文字列へ整形する（例: 1536 → "1.5 KB"）。
         * @param {number} bytes 0以上の有限数
         * @param {Object} [options]
         * @param {number} [options.decimals] 小数桁数（既定: 1）
         * @returns {string} 整形結果
         * @throws {Error} INVALID_ARGUMENT 負数・非有限値・数値以外
         */
        formatBytes: function (bytes, options) {
            var settings = options || {};
            if (!isFiniteNumber(bytes) || bytes < 0) {
                throw createError(ERROR_CODES.INVALID_ARGUMENT, 'bytes must be a non-negative finite number.');
            }

            var decimals = settings.decimals === undefined ? DEFAULT_BYTE_DECIMALS : settings.decimals;
            if (!isFiniteNumber(decimals) || decimals < 0) {
                throw createError(ERROR_CODES.INVALID_ARGUMENT, 'decimals must be a non-negative finite number.');
            }

            if (bytes === 0) return '0 ' + BYTE_UNITS[0];

            // 単位表を超える巨大な値でも undefined にならないよう、添字を上限で丸める
            var unitIndex = Math.min(
                BYTE_UNITS.length - 1,
                Math.floor(Math.log(bytes) / Math.log(BYTES_PER_UNIT))
            );
            var value = bytes / Math.pow(BYTES_PER_UNIT, unitIndex);
            return value.toFixed(Math.floor(decimals)) + ' ' + BYTE_UNITS[unitIndex];
        }
    };

    // =============================================
    // clipboard: クリップボード操作
    // =============================================

    /**
     * document.execCommand('copy') によるフォールバック。
     * 一時textareaは成否に関わらず必ず取り除く。
     * @returns {boolean} コピーの成否
     */
    function copyTextWithExecCommand(text) {
        var textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.setAttribute('readonly', 'readonly');
        textarea.style.position = 'fixed';
        textarea.style.left = '-9999px';
        textarea.style.opacity = '0';
        document.body.appendChild(textarea);

        var success = false;
        try {
            textarea.select();
            success = document.execCommand('copy');
        } catch (e) {
            success = false;
        } finally {
            document.body.removeChild(textarea);
        }
        return success;
    }

    var clipboardNamespace = {
        /**
         * テキストをクリップボードへコピーする。
         * Clipboard APIを優先し、使えない場合・失敗した場合はexecCommandへフォールバックする。
         * 通知は行わない（成否の表示は呼び出し元の責任）。
         * @param {string} text コピーする文字列（文字列以外はINVALID_ARGUMENTでreject）
         * @returns {Promise<boolean>} 成功でtrue、失敗でfalse（例外は外へ出さない）
         */
        copyText: function (text) {
            if (typeof text !== 'string') {
                return Promise.reject(
                    createError(ERROR_CODES.INVALID_ARGUMENT, 'text must be a string.')
                );
            }

            if (navigator.clipboard && navigator.clipboard.writeText) {
                return navigator.clipboard.writeText(text).then(function () {
                    return true;
                }, function () {
                    // 権限・非セキュアコンテキスト等で失敗した場合も、旧APIで再試行する
                    return copyTextWithExecCommand(text);
                });
            }

            return Promise.resolve(copyTextWithExecCommand(text));
        }
    };

    // =============================================
    // canvas: Canvasの書き出しと座標変換
    // =============================================

    var canvasNamespace = {
        /**
         * CanvasをBlobへ変換する。toBlob()がnullを返した場合は成功扱いにせずrejectする。
         * @param {HTMLCanvasElement} canvas 対象canvas
         * @param {string} [mimeType] 出力MIME（既定: image/png）
         * @param {number} [quality] JPEG/WebP等の品質（0〜1）
         * @returns {Promise<Blob>} INVALID_ARGUMENT / CANVAS_EXPORT_FAILED でreject
         */
        toBlob: function (canvas, mimeType, quality) {
            return new Promise(function (resolve, reject) {
                if (!isCanvasLike(canvas)) {
                    reject(createError(ERROR_CODES.INVALID_ARGUMENT, 'canvas must be an HTMLCanvasElement.'));
                    return;
                }

                canvas.toBlob(function (blob) {
                    if (!blob) {
                        reject(createError(ERROR_CODES.CANVAS_EXPORT_FAILED, 'canvas.toBlob() returned null.'));
                        return;
                    }
                    resolve(blob);
                }, mimeType || 'image/png', quality);
            });
        },

        /**
         * CanvasをPNGとして保存する。成功通知は行わないため、
         * 呼び出し元が解決後に通知できる。
         * @param {HTMLCanvasElement} canvas 対象canvas
         * @param {string} [fileName] 保存名（既定: image.png）
         * @returns {Promise<void>} 保存の発火まで完了したら解決
         */
        downloadPng: function (canvas, fileName) {
            return canvasNamespace.toBlob(canvas, 'image/png').then(function (blob) {
                filesNamespace.downloadBlob(blob, fileName || DEFAULT_PNG_FILE_NAME);
            });
        },

        /**
         * マウス／ポインタ／タッチイベントの座標を、Canvasの内部ピクセル座標へ変換する。
         * CSS表示サイズと内部サイズの倍率を反映する。
         * @param {HTMLCanvasElement} canvas 対象canvas
         * @param {MouseEvent|PointerEvent|TouchEvent} event 対象イベント
         * @param {boolean} [clamp] trueならCanvas範囲内へ丸める
         * @returns {{x:number, y:number}|null} 座標を取り出せないイベントではnull
         * @throws {Error} INVALID_ARGUMENT canvas・eventが不正な場合
         */
        getPointerPosition: function (canvas, event, clamp) {
            if (!isCanvasLike(canvas)) {
                throw createError(ERROR_CODES.INVALID_ARGUMENT, 'canvas must be an HTMLCanvasElement.');
            }
            if (!event) {
                throw createError(ERROR_CODES.INVALID_ARGUMENT, 'event is required.');
            }

            // TouchEventはtouches（操作中）とchangedTouches（touchend）で座標の在り処が変わる
            var source = event;
            if (event.touches && event.touches.length) {
                source = event.touches[0];
            } else if (event.changedTouches && event.changedTouches.length) {
                source = event.changedTouches[0];
            }
            if (!isFiniteNumber(source.clientX) || !isFiniteNumber(source.clientY)) {
                return null;
            }

            var rect = canvas.getBoundingClientRect();
            if (!(rect.width > 0) || !(rect.height > 0)) return null;

            var x = (source.clientX - rect.left) * (canvas.width / rect.width);
            var y = (source.clientY - rect.top) * (canvas.height / rect.height);
            if (clamp) {
                x = Math.max(0, Math.min(canvas.width, x));
                y = Math.max(0, Math.min(canvas.height, y));
            }
            return { x: x, y: y };
        }
    };

    // =============================================
    // image: 画像ファイルの読込と寸法計算
    // =============================================

    var imageNamespace = {
        /**
         * 画像ファイルをHTMLImageElementとして読み込む。
         * @param {File|Blob} file 対象ファイル
         * @param {Object} [options]
         * @param {number} [options.maxBytes] 上限バイト数
         * @returns {Promise<HTMLImageElement>} INVALID_FILE / SIZE_LIMIT / READ_ERROR / IMAGE_LOAD_FAILED でreject
         */
        loadFile: function (file, options) {
            return filesNamespace.readAsDataUrl(file, options).then(function (dataUrl) {
                return new Promise(function (resolve, reject) {
                    var image = new Image();
                    image.onload = function () {
                        resolve(image);
                    };
                    image.onerror = function () {
                        reject(createError(ERROR_CODES.IMAGE_LOAD_FAILED, 'Failed to decode the image.'));
                    };
                    image.src = dataUrl;
                });
            });
        },

        /**
         * 画像のMIMEタイプから保存名に使う拡張子を求める（image/jpegは jpg へ正規化）。
         * @param {string} mimeType 判定するMIMEタイプ（大文字・パラメータ付きも可）
         * @param {string} [fallbackExtension] 対応表に無いときに使う拡張子（既定: bin）
         * @returns {string} 拡張子（ドットなし）
         */
        getExtensionForMimeType: function (mimeType, fallbackExtension) {
            var normalized = String(mimeType == null ? '' : mimeType)
                .toLowerCase()
                .split(';')[0]
                .trim();

            if (Object.prototype.hasOwnProperty.call(IMAGE_EXTENSION_BY_MIME_TYPE, normalized)) {
                return IMAGE_EXTENSION_BY_MIME_TYPE[normalized];
            }
            return fallbackExtension || DEFAULT_MIME_FALLBACK_EXTENSION;
        },

        /**
         * 元の寸法と倍率から出力寸法を求める。
         * 端数は切り捨て、細長い画像で短辺が0pxにならないよう最低1pxへ丸める。
         * @param {number} width 元の幅（1以上の有限数）
         * @param {number} height 元の高さ（1以上の有限数）
         * @param {number} scale 倍率（0より大きい有限数）
         * @param {Object} [options]
         * @param {number} [options.maxWidth] 出力幅の上限（超える場合は縦横比を保って収める）
         * @param {number} [options.maxHeight] 出力高さの上限
         * @returns {{width:number, height:number}}
         * @throws {Error} INVALID_ARGUMENT 寸法・倍率が不正な場合
         */
        calculateScaledSize: function (width, height, scale, options) {
            var settings = options || {};
            if (!isFiniteNumber(width) || width <= 0 || !isFiniteNumber(height) || height <= 0) {
                throw createError(ERROR_CODES.INVALID_ARGUMENT, 'width and height must be positive finite numbers.');
            }
            if (!isFiniteNumber(scale) || scale <= 0) {
                throw createError(ERROR_CODES.INVALID_ARGUMENT, 'scale must be a positive finite number.');
            }

            var effectiveScale = scale;
            if (isFiniteNumber(settings.maxWidth) && settings.maxWidth > 0) {
                effectiveScale = Math.min(effectiveScale, settings.maxWidth / width);
            }
            if (isFiniteNumber(settings.maxHeight) && settings.maxHeight > 0) {
                effectiveScale = Math.min(effectiveScale, settings.maxHeight / height);
            }

            return {
                width: Math.max(MIN_SCALED_SIZE_PX, Math.floor(width * effectiveScale)),
                height: Math.max(MIN_SCALED_SIZE_PX, Math.floor(height * effectiveScale))
            };
        }
    };

    // =============================================
    // dom: 画面に依存しないDOM補助
    // =============================================

    // 初期化済みの要素。同じ要素へイベントを二重登録しないために保持する。
    var initializedTabGroups = typeof WeakSet === 'function' ? new WeakSet() : null;
    var dropdownDisposers = typeof WeakMap === 'function' ? new WeakMap() : null;

    var domNamespace = {
        /**
         * 要素のdisabled状態を設定する。値は必ず真偽値へ正規化する
         * （undefinedを渡して「設定されない」事故を防ぐ）。
         * disabledプロパティを持たない要素は無視する。
         * @param {HTMLElement|Array|NodeList} target 対象要素（複数可）
         * @param {*} disabled 無効にするかどうかの条件式
         */
        setDisabled: function (target, disabled) {
            var value = Boolean(disabled);
            toElementArray(target).forEach(function (element) {
                if ('disabled' in element) element.disabled = value;
            });
        },

        /**
         * タブ列（role="tab"等）の選択状態とキーボード操作を共通化する。
         * activeクラス・aria-selected・tabindexの更新と、クリック／左右矢印／Home／Endを担う。
         * 表示領域の切替は画面ごとに異なるため onSwitch へ委ねる。
         * @param {Object} options
         * @param {NodeList|Array} options.tabs タブ要素の集合
         * @param {function(HTMLElement): *} [options.getValue] タブから切替対象の値を取り出す（既定: 要素自身）
         * @param {function(*, HTMLElement)} [options.onSwitch] 選択時に値とタブ要素を受け取る
         * @param {string} [options.activeClass] 選択中を表すクラス名（既定: active）
         * @returns {function(): void} イベントを解除する関数
         */
        initTabGroup: function (options) {
            var settings = options || {};
            var tabs = toElementArray(settings.tabs);
            if (!tabs.length) return function () {};

            var activeClass = settings.activeClass || DEFAULT_TAB_ACTIVE_CLASS;
            var getValue = typeof settings.getValue === 'function'
                ? settings.getValue
                : function (tab) { return tab; };
            var onSwitch = typeof settings.onSwitch === 'function' ? settings.onSwitch : null;

            // 既に初期化済みのタブ列は二重登録しない
            if (initializedTabGroups && initializedTabGroups.has(tabs[0])) {
                return function () {};
            }
            if (initializedTabGroups) initializedTabGroups.add(tabs[0]);

            function select(tab, moveFocus) {
                tabs.forEach(function (candidate) {
                    var selected = candidate === tab;
                    candidate.classList.toggle(activeClass, selected);
                    candidate.setAttribute('aria-selected', String(selected));
                    candidate.setAttribute('tabindex', selected ? '0' : '-1');
                });
                if (moveFocus) tab.focus();
                if (onSwitch) onSwitch(getValue(tab), tab);
            }

            function handleClick(event) {
                select(event.currentTarget, false);
            }

            // 選択中のタブだけをTabキー移動の対象とし、タブ列内は左右矢印・Home・Endで移動する
            function handleKeydown(event) {
                var currentIndex = tabs.indexOf(event.currentTarget);
                var nextIndex;

                if (event.key === 'ArrowRight') nextIndex = (currentIndex + 1) % tabs.length;
                else if (event.key === 'ArrowLeft') nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
                else if (event.key === 'Home') nextIndex = 0;
                else if (event.key === 'End') nextIndex = tabs.length - 1;
                else return;

                event.preventDefault();
                select(tabs[nextIndex], true);
            }

            tabs.forEach(function (tab) {
                tab.addEventListener('click', handleClick);
                tab.addEventListener('keydown', handleKeydown);
            });

            // 初期表示：マークアップ上の選択済みタブ（無ければ先頭）へ状態と表示を揃える
            var initial = tabs.filter(function (tab) {
                return tab.classList.contains(activeClass);
            })[0] || tabs[0];
            select(initial, false);

            return function dispose() {
                tabs.forEach(function (tab) {
                    tab.removeEventListener('click', handleClick);
                    tab.removeEventListener('keydown', handleKeydown);
                });
                if (initializedTabGroups) initializedTabGroups.delete(tabs[0]);
            };
        },

        /**
         * <details>で作った簡易ドロップダウンを、メニュー内の項目を選んだときと
         * メニュー外をクリックしたときに閉じる（<details>は既定でどちらでも閉じない）。
         * Escキーは画面側の用途と競合しうるため、既定では扱わない。
         * @param {HTMLElement} detailsElement 対象の<details>要素（無ければ何もしない）
         * @param {Object} [options]
         * @param {string} [options.closeSelector] メニュー内で閉じる対象のセレクタ（既定: button）
         * @returns {function(): void} イベントを解除する関数
         */
        initDropdownMenu: function (detailsElement, options) {
            if (!isElement(detailsElement)) return function () {};

            // 同じ要素へ二重に登録しない（既に初期化済みなら既存の解除関数を返す）
            if (dropdownDisposers && dropdownDisposers.has(detailsElement)) {
                return dropdownDisposers.get(detailsElement);
            }

            var settings = options || {};
            var closeSelector = settings.closeSelector || DEFAULT_DROPDOWN_CLOSE_SELECTOR;

            function handleMenuClick(event) {
                var target = event.target;
                if (target && typeof target.closest === 'function' && target.closest(closeSelector)) {
                    detailsElement.open = false;
                }
            }

            function handleDocumentClick(event) {
                // summaryのクリックで開く場合、この時点ではまだopen=falseのため閉じ処理は走らない
                if (!detailsElement.open) return;
                if (detailsElement.contains(event.target)) return;
                detailsElement.open = false;
            }

            detailsElement.addEventListener('click', handleMenuClick);
            document.addEventListener('click', handleDocumentClick);

            var dispose = function () {
                detailsElement.removeEventListener('click', handleMenuClick);
                document.removeEventListener('click', handleDocumentClick);
                if (dropdownDisposers) dropdownDisposers.delete(detailsElement);
            };
            if (dropdownDisposers) dropdownDisposers.set(detailsElement, dispose);
            return dispose;
        }
    };

    // =============================================
    // text: 文字列処理
    // =============================================

    var textNamespace = {
        /**
         * HTMLとして解釈されると困る文字をエスケープする。
         * @param {*} text 対象（null/undefinedは空文字）
         * @returns {string} エスケープ済み文字列
         */
        escapeHtml: function (text) {
            if (text == null) return '';
            return String(text)
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;')
                .replace(/'/g, '&#039;');
        }
    };

    // =============================================
    // 公開
    // =============================================

    global.LGToolkit = {
        version: VERSION,
        errorCodes: ERROR_CODES,
        async: asyncNamespace,
        files: filesNamespace,
        clipboard: clipboardNamespace,
        canvas: canvasNamespace,
        image: imageNamespace,
        dom: domNamespace,
        text: textNamespace
    };
}(typeof window !== 'undefined' ? window : this));
