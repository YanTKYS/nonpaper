# 第三者ソフトウェアに関する通知

NonPaper 自体（`src/`、`tests/`、`docs/` ほかリポジトリ内の自作部分）は、ルートの [LICENSE](LICENSE)（MIT License）で提供します。この文書は、MIT License の対象ではない第三者の成果物と、その元のライセンスを示します。

## GitHub Release の ZIP に含まれる第三者ソフトウェア

GitHub Release の ZIP は、.NET 8 の自己完結型（self-contained）・単一ファイルで発行します。そのため `NonPaper.exe` に次のランタイムが含まれます。

| 名称 | 提供元 | ライセンス |
| --- | --- | --- |
| .NET Runtime（Microsoft.NETCore.App） | Microsoft / .NET Foundation and Contributors | MIT License |
| ASP.NET Core Runtime（Microsoft.AspNetCore.App） | Microsoft / .NET Foundation and Contributors | MIT License |

これらのライセンス全文と、ランタイムが取り込む第三者ソフトウェアの通知は、ZIP 内の `third-party-licenses\` フォルダーに、リリース作成時に取得した .NET ランタイムパッケージの `LICENSE.txt` と `THIRD-PARTY-NOTICES.TXT` として同梱します。元のライセンスと通知は変更せずそのまま保持してください。

上記のランタイム以外の第三者コード・ライブラリ・フォント・画像は ZIP に含めていません。IIS 側の ASP.NET Core Hosting Bundle（`AspNetCoreModuleV2`）は ZIP に含まれず、Microsoft のライセンス条件に従って別途導入します。

## 開発・テストにだけ使用するもの（ZIP には含まれません）

| 名称 | ライセンス |
| --- | --- |
| xUnit（`xunit`、`xunit.runner.visualstudio`） | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT License |

これらは `tests/` のビルドと実行にだけ NuGet から取得します。リポジトリにも ZIP にも含まれません。

## 外部のコード・素材について

* PDF の表示には、現行ブラウザー内蔵の PDF レンダラーを利用します。PDF.js などの JavaScript ライブラリや CDN 配信物、Web フォント、外部の画像・アイコンはリポジトリに含めていません。
* `src/NonPaper/wwwroot/js/lgtoolkit.js` は、このリポジトリの自作の汎用ライブラリであり、MIT License の対象です。

## MIT License の対象外

* 利用者がアップロードする PDF、会議名・説明などの会議情報、およびアプリが保存するデータ（`data/` 配下など）は利用者のデータです。MIT License は適用されず、権利は元の権利者に帰属します。
