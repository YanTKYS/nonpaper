# NonPaper IIS配置手順

閉域内のWindows Server（IIS）へNonPaperを配置し、実機確認を行うための手順書です。実機確認は最初からIIS上で行い、Kestrelの単体起動は使用しません。

## 1. 想定構成

* 配置先：閉域内のWindows Server + IIS
* 実機確認：IIS上で実施（Kestrel単体起動は使用しない）
* 初期実機確認：HTTP、ポート`5080`の独立Webサイト
* 本番運用時のHTTPS化：[15. HTTPS化](#15-https化)を参照
* リリース成果物の取得：インターネット接続環境でGitHub Releaseを作成し、生成されたZIPを閉域へ持ち込む

```text
[インターネット接続環境]
  GitHub Actions (release.yml) → GitHub Release (ZIPを添付)
        │
        │ ZIPをダウンロードし、USBメモリ等で持ち込み
        ▼
[閉域内 Windows Server + IIS]
  C:\inetpub\nonpaper\        ← アプリケーション本体（ZIPの内容）
  C:\ProgramData\NonPaper\    ← 会議データ（events）
```

## 2. 前提ソフトウェア

* Windows ServerまたはIISを利用できるWindows端末
* IIS（Webサーバー機能、および「Webサーバー」役割サービス）
* ASP.NET Core Hosting Bundle 8（`AspNetCoreModuleV2`を含む）
* Chrome、Edge、Firefox等の現行版ブラウザー
* 作業用の管理者権限
* リリースZIPを閉域へ持ち込む手段（USBメモリ、ファイル転送サーバー等）

NonPaperのリリースZIPは自己完結型（self-contained）発行のため.NET Runtimeを内包していますが、これはアプリケーションプロセス自身がRuntimeを持つだけであり、IISとASP.NET Coreアプリケーションを接続するモジュール（`AspNetCoreModuleV2`）は含まれません。IIS側にASP.NET Core Hosting Bundleを別途導入する必要があります。

Hosting Bundle導入後は、IISまたはサーバー自体の再起動が必要になる場合があります。導入直後にアプリが起動しない場合は、まず再起動を試してください。

## 3. リリースZIPの確認

配置作業を始める前に、GitHub Releaseから取得したZIPを展開し、内容を確認します。`release.yml`は展開後に必要ファイルが揃っていることを自動検証したうえでZIPを作成していますが、配置前にも目視で確認してください。

展開後、最低限次が含まれていることを確認します。

```text
NonPaper.exe
wwwroot\
appsettings.json
web.config
README.md
```

`wwwroot`には最低限次が含まれていることを確認します。

```text
index.html
manage.html
upload.html
meeting.html
css\   （CSSファイルを含む）
js\    （JavaScriptファイルを含む）
```

自己完結型単一ファイル発行の結果によっては、上記に加えてネイティブライブラリ等の追加ファイルが含まれる場合があります。ZIP内のファイルは取捨選択せず、原則そのまま配置先へコピーしてください。

いずれかのファイルが不足している場合は、そのままIISへ配置しないでください。リリースワークフロー（`.github/workflows/release.yml`）はパッケージ化の前後で必須ファイルの存在を検証するため、不足はワークフロー側の不具合です。検証条件と発行設定を確認してください。

## 4. フォルダー構成

アプリケーションファイルと会議データを分離し、それぞれ独立したフォルダーへ配置します。

### アプリ配置先

```text
C:\inetpub\nonpaper\
├─ NonPaper.exe
├─ appsettings.json
├─ web.config
├─ wwwroot\
└─ README.md
```

（自己完結型発行の結果に応じて、上記以外のファイルが含まれる場合はそのまま配置します。）

### データ保存先

```text
C:\ProgramData\NonPaper\
└─ events\
```

アプリケーション更新時に会議データを誤って削除しないよう、会議データは`C:\inetpub\nonpaper`配下には保存しません。アプリ配置先とデータ保存先を明確に分離することが、この構成の目的です。

## 5. データ保存先の設定

`appsettings.json`の`Storage:Root`を、閉域環境のデータ保存先へ絶対パスで指定します。リリースZIPに含まれる既定の`appsettings.json`はリポジトリ内の相対パス（`data/events`）のままなので、配置時に次のように統合してください（ファイル全体を上書きするのではなく、既存のキーを書き換えます）。

```json
{
  "Storage": {
    "Root": "C:\\ProgramData\\NonPaper\\events"
  },
  "Upload": {
    "MaxFileSizeBytes": 104857600,
    "MaxDocuments": 20
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

各設定の意味は次のとおりです。

| キー | 意味 |
| --- | --- |
| `Storage:Root` | イベント（会議）データとPDFの保存先ルートフォルダー |
| `Upload:MaxFileSizeBytes` | PDF1ファイルあたりのアップロード上限バイト数（既定値: 104857600 = 100 MiB） |
| `Upload:MaxDocuments` | 1イベントに登録できるPDFの最大件数（既定値: 20） |

`C:\ProgramData\NonPaper\events`フォルダーは、この時点で作成しておきます（存在しない場合、初回のイベント作成時にエラーとなります）。

```powershell
New-Item -ItemType Directory -Path "C:\ProgramData\NonPaper\events" -Force
```

## 6. IISアプリケーションプールの作成

NonPaper専用のアプリケーションプールを作成します。

```text
アプリケーションプール名: NonPaperPool
```

| 項目 | 設定 |
| --- | --- |
| .NET CLRバージョン | マネージドコードなし |
| マネージドパイプラインモード | 統合 |
| 32ビットアプリケーションの有効化 | False |
| ID | ApplicationPoolIdentity |

初期の実機確認では上記の既定設定で問題ありません。安定運用時には、次の設定変更も検討してください。

* 開始モード：`AlwaysRunning`
* アイドルタイムアウト：`0`（無効化）
* サイトのプリロード：有効

## 7. IIS Webサイトの作成

初回の実機確認では、既存サイト配下ではなく独立したWebサイトとして作成することを推奨します。

| 項目 | 設定 |
| --- | --- |
| サイト名 | NonPaper |
| 物理パス | `C:\inetpub\nonpaper` |
| アプリケーションプール | `NonPaperPool` |
| バインド | http |
| IPアドレス | 未使用のIPすべて |
| ポート | 5080 |
| ホスト名 | 空欄 |

作成後、次のURLでアクセスできることを確認します。

```text
http://localhost:5080/
http://サーバー名:5080/
```

既存サイト配下へ`/nonpaper/`のような仮想アプリケーションとして配置することも可能ですが、NonPaperはルート相対パスでAPIやリソースを参照している箇所があるため、独立サイトでのルートパス動作を確認できるまでは推奨しません。仮想アプリケーションとして配置する場合は、画面遷移・API呼び出し・静的ファイル参照が正しく動作するか別途確認してください。

## 8. NTFS権限の設定

アプリ配置先とデータ保存先で、付与する権限を明確に分けます。

### アプリ配置先（`C:\inetpub\nonpaper`）

アプリケーションプールのIDには読み取り・実行権限のみを付与します。書込権限は原則付与しません。

### データ保存先（`C:\ProgramData\NonPaper\events`）

次のアカウントに変更（Modify）権限を付与します。

```text
IIS AppPool\NonPaperPool
```

PowerShellでの設定例です。

```powershell
$path = "C:\ProgramData\NonPaper\events"
$identity = "IIS AppPool\NonPaperPool"
$acl = Get-Acl $path
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $identity,
    "Modify",
    "ContainerInherit,ObjectInherit",
    "None",
    "Allow"
)
$acl.SetAccessRule($rule)
Set-Acl -Path $path -AclObject $acl
```

設定内容の確認例です。

```powershell
(Get-Acl "C:\ProgramData\NonPaper\events").Access |
  Where-Object IdentityReference -Like "*NonPaperPool*"
```

## 9. web.configとアップロード上限

リリースZIPに含まれる`web.config`は`dotnet publish`が自動生成したものであり、自己完結型EXEをIISで起動するために必要な設定（`AspNetCoreModuleV2`ハンドラー、`processPath`等）を含んでいます。この`web.config`をそのまま使用してください。手作業でのテンプレート差し替えは不要です。

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore"
             path="*"
             verb="*"
             modules="AspNetCoreModuleV2"
             resourceType="Unspecified" />
      </handlers>
      <security>
        <requestFiltering>
          <requestLimits maxAllowedContentLength="262144000" />
        </requestFiltering>
      </security>
      <aspNetCore processPath=".\NonPaper.exe"
                  arguments=""
                  stdoutLogEnabled="false"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
```

（`processPath`は自己完結型EXEへの相対パスです。`dotnet`コマンドは指定しません。）

`release.yml`は発行後の`web.config`へ`requestLimits`（`maxAllowedContentLength`）を自動的に追加しているため、リリースZIP内の`web.config`にはこの設定が最初から含まれています。含まれていない場合はリリースワークフロー（`.github/workflows/release.yml`）の不具合です。

### アップロード上限について

NonPaper側の1ファイル上限が100 MiB（`Upload:MaxFileSizeBytes`）であっても、IIS側の`maxAllowedContentLength`が先にリクエストを拒否する場合があります。

* NonPaper側の`Upload:MaxFileSizeBytes`：PDF **1ファイル単位** の上限
* IISの`maxAllowedContentLength`：HTTPリクエスト **全体** の上限（バイト数）
* 複数PDFを同時アップロードする画面では、IIS側は合計容量を考慮する必要があります

初期値として`maxAllowedContentLength="262144000"`（250 MiB）を設定していますが、大容量PDFを複数同時に登録する運用では不足する可能性があるため、実運用に合わせて`web.config`の値を調整してください。

ASP.NET Core側（Kestrel/IIS統合）の要求本文サイズ上限は既定で30 MB前後ですが、NonPaperはPDF登録APIに限り`Upload:MaxFileSizeBytes` × `Upload:MaxDocuments`まで上限を引き上げます。したがって`appsettings.json`の値を変更すればアプリケーション側の上限も追従し、追加の設定は不要です。IIS側の`maxAllowedContentLength`だけは`web.config`で別途調整してください。

## 10. サイト起動と接続確認

1. IISマネージャーで`NonPaper`サイトとアプリケーションプール`NonPaperPool`が「開始」状態であることを確認します。
2. サーバー上のブラウザーで`http://localhost:5080/`へアクセスし、トップページが表示されることを確認します。

別端末からアクセスする場合は、Windowsファイアウォールでポート`5080`を許可します。

```powershell
New-NetFirewallRule `
  -DisplayName "NonPaper IIS TCP 5080" `
  -Direction Inbound `
  -Protocol TCP `
  -LocalPort 5080 `
  -Action Allow
```

確認後、別端末から`http://サーバー名:5080/`へアクセスできることを確認します。

不要になった場合の削除例です。

```powershell
Remove-NetFirewallRule -DisplayName "NonPaper IIS TCP 5080"
```

## 11. 実機確認項目

IIS上で次の一連の動作を確認します。

### 配置確認

* [ ] トップページが表示される
* [ ] CSSとJavaScriptが読み込まれる
* [ ] ブラウザーの開発者ツールに404がない
* [ ] `wwwroot`の各画面（トップ／管理／PDF登録／会議画面）へ遷移できる
* [ ] イベント作成時にデータフォルダーが生成される

### 主催者操作

* [ ] イベントを作成できる
* [ ] 管理用URLと参加者用URLをコピーできる
* [ ] PDFを1件登録できる
* [ ] PDFを複数同時登録できる
* [ ] 資料名を変更できる
* [ ] 資料順を変更できる
* [ ] 資料を削除できる
* [ ] 公開できる
* [ ] 下書きへ戻せる
* [ ] 会議を終了できる
* [ ] 終了後に再公開できない
* [ ] イベントを削除できる

### 参加者操作

* [ ] 公開前は閲覧できない
* [ ] 公開後は別端末から閲覧できる
* [ ] 資料を切り替えられる
* [ ] ページ移動できる
* [ ] 拡大・縮小できる
* [ ] 全画面表示できる
* [ ] 通常画面に印刷・ダウンロードボタンがない
* [ ] 会議終了後、最大30秒程度で閲覧が終了する
* [ ] イベント削除後はURLを利用できない

### 保存状態の確認

イベント作成後、次のフォルダー・ファイルが生成されることを確認します。

```text
C:\ProgramData\NonPaper\events\{eventId}\
├─ event.json
└─ documents\
```

PDF登録後：

```text
documents\{documentId}.pdf
```

イベント削除後：

```text
{eventId}フォルダーが存在しない
```

## 12. アプリケーション更新手順

単にリリースZIPを既存フォルダーへ上書きすると、旧バージョンで使われていた不要なファイルが残る可能性があるため、原則として新しいフォルダーへ展開してから差し替えます。

1. 会議が利用されていない時間帯を選びます。
2. IISサイトまたはアプリケーションプールを停止します。
3. `C:\inetpub\nonpaper`をバックアップします（別フォルダーへコピー、またはアーカイブ化）。
4. 新しいリリースZIPを別フォルダー（例：`C:\inetpub\nonpaper_new`）へ展開します。
5. `appsettings.json`の環境固有設定（`Storage:Root`等、[5. データ保存先の設定](#5-データ保存先の設定)で行った変更）を新しいフォルダーへ引き継ぎます。
6. `C:\inetpub\nonpaper`の内容を、展開した新しいフォルダーの内容で差し替えます。
7. データ保存先`C:\ProgramData\NonPaper\events`には手を加えません。
8. IISサイトまたはアプリケーションプールを開始します。
9. トップ画面表示、イベント作成、既存イベントのPDF閲覧を確認します。

## 13. バックアップと復元

バックアップ対象は会議データのみです。

```text
C:\ProgramData\NonPaper\events
```

* イベント更新中（PDF登録・公開操作等）のバックアップは避けてください。
* アプリ停止中、または利用のない時間帯に、`events`フォルダー全体を一体としてバックアップします。
* 復元もイベントフォルダー単位（`{eventId}`フォルダーごと）でJSONとPDFをまとめて復元します。

アプリケーション本体（実行ファイル一式）はGitHub Releaseから何度でも再取得できるため、バックアップ対象は会議データを優先してください。

## 14. 障害時の確認

### 500.19

確認事項：

* `web.config`の構文が正しいか（XMLとして壊れていないか）
* ASP.NET Core Hosting Bundleが導入されているか
* `AspNetCoreModuleV2`がサーバーに存在するか（Hosting Bundle導入後にIIS/サーバーを再起動したか）

### 500.30（アプリ起動失敗）

一時的に次を設定し、詳細ログを確認します。

```xml
stdoutLogEnabled="true"
```

ログ出力先：

```text
C:\inetpub\nonpaper\logs
```

`logs`フォルダーにはアプリケーションプールの変更権限を付与してください。原因確認後は必ず`stdoutLogEnabled="false"`へ戻します。stdoutログは自動ローテーションされないため、常時有効のままにはしないでください。

### 403または404

確認事項：

* `wwwroot`フォルダーが存在するか
* `wwwroot\index.html`が存在するか
* IISサイトの物理パスが一階層ずれていないか（ZIPの親フォルダーごと配置していないか）
* CSS・JavaScriptの参照パスが正しいか

### イベント作成時の500

確認事項：

* `appsettings.json`の`Storage:Root`が正しいパスを指しているか
* データ保存先フォルダー（`C:\ProgramData\NonPaper\events`）が存在するか
* `IIS AppPool\NonPaperPool`にデータ保存先の変更権限があるか
* `appsettings.json`のJSON構文が正しいか

### PDFアップロード時の413

確認事項：

* NonPaper側のファイル上限（`Upload:MaxFileSizeBytes`）と件数上限（`Upload:MaxDocuments`）
* IISの`maxAllowedContentLength`（[9. web.configとアップロード上限](#9-webconfigとアップロード上限)）
* 複数ファイル同時アップロード時の合計容量

NonPaperが返す413には、日本語の理由（ファイルサイズ超過、件数超過、要求全体の容量超過）が含まれます。理由が表示されずIISやブラウザーの既定エラー画面になる場合は、IIS側の`maxAllowedContentLength`で拒否されています。

## 15. HTTPS化

閉域環境での初期実機確認はHTTPで問題ありませんが、本番運用ではHTTPS化を行ってください。

* IISに証明書をバインドし、サイトのバインドへHTTPS（既定443、または任意のポート）を追加します。
* 社内認証局等で発行した証明書を利用できます。
* HTTPS化後は、HTTPアクセスをHTTPSへリダイレクトする設定、または不要なHTTPバインドの削除を検討してください。
* 管理用URLは管理トークンを含む秘密情報であるため、本番運用ではHTTPSの利用を強く推奨します（README「セキュリティと制約」も参照）。

## 16. IIS配置時の注意事項

* 既存サイト配下への `/nonpaper/` 仮想アプリケーション配置は、ルートパス依存の動作確認が済むまで推奨しません。
* 管理用URLは管理トークンを含む秘密情報です。IISのログでクエリ文字列を記録している場合、ログの取り扱いに注意してください。
* 会議データはアプリ配置先の外（`C:\ProgramData\NonPaper\events`）にあります。アプリ更新の対象へこのフォルダーを含めないでください。

イベントの状態遷移、PDFのダウンロード・印刷に関する性質など、システムとしての仕様は [README.md](../README.md) を正本とします。
