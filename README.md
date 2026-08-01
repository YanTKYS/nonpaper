# NonPaper v0.1.0

庁内ネットワークで完結する、ブラウザー型の簡易ペーパーレス会議システムです。主催者はイベント作成、複数PDFの登録・名称/順序変更、公開、終了、削除を行い、参加者は公開中の資料を閲覧できます。SPA、データベース、外部API、CDNは使用しません。

## 動作環境と起動

* Windows Server/Windows、IISまたはASP.NET Core Kestrel
* .NET 8 SDK（配置先にはASP.NET Core Hosting Bundleまたは.NET 8 Runtime）
* 現行版のChrome、Edge、Firefox

```powershell
dotnet restore NonPaper.sln
dotnet build NonPaper.sln -c Release
dotnet run --project src/NonPaper/NonPaper.csproj
```

表示されたURLへアクセスします。閉域環境への配置時は、インターネット接続環境でrestore/publishした成果物を持ち込みます。IISではHosting Bundleを導入し、`dotnet publish -c Release` の出力をアプリケーションとして登録し、アプリプールにデータディレクトリの変更権限を付与してください。HTTPSの利用を推奨します。

## 利用手順

1. トップ画面で会議名、説明、開催・終了日時を入力します。
2. 一度だけ表示される管理用URLを安全な場所へコピーします。参加者用URLもコピーできます。
3. PDF登録画面で複数ファイルを選択またはドロップし、名称と順序を調整します。
4. 管理画面で「公開する」を押し、参加者へ参加者用URLを案内します。
5. 参加者は資料を切り替え、ページ移動、拡縮、全画面表示を利用します。
6. 管理画面で会議を終了すると、参加者画面は最大30秒以内に閲覧を終了します。
7. 会議名を確認するダイアログを経てイベントを削除します。

## 保存、設定、バックアップ

イベントは既定で `src/NonPaper/data/events/{eventId}/event.json`、PDFはその配下の `documents/{documentId}.pdf` に保存します。`appsettings.json` の `Storage:Root`、`Upload:MaxFileSizeBytes`（既定100 MiB）、`Upload:MaxDocuments`（既定20）を変更できます。データフォルダー全体を、アプリ停止中または更新がない時間帯に一体としてバックアップしてください。復元もイベントフォルダー単位で行います。

削除は認証後に状態を `deleting` として安全に保存してから、PDF、JSON、イベントフォルダーを削除します。既にブラウザーへ読み込まれたデータまでは消去できません。

## セキュリティと制約

イベント/資料IDは128ビット乱数、管理トークンは256ビット乱数です。トークンは作成時だけ返し、JSONにはSHA-256ハッシュのみ保存して固定時間比較します。管理用URLはパスワード同等の秘密情報であり、メール転送、履歴共有、アクセスログへの記録に注意してください。管理変更APIはBearerトークンと同一オリジンJavaScriptだけが付与するカスタムヘッダーを要求します。

PDFは静的領域に置かず、公開状態と資料所属を確認するRange対応APIから `inline` 相当で配信します。ID形式を限定し、外部ファイル名をパスに使用せず、拡張子と `%PDF-` シグネチャ、サイズ、件数を検査します。JSONは一時ファイルへの書込み後に原子的に置換し、イベント単位で排他します。画面は `textContent` により利用者入力を表示します。

通常の画面操作では、PDFのダウンロードおよび印刷機能を提供しません。ツールバーを隠し、ダウンロード/印刷ボタンを設けず、右クリックとCtrl+Pを抑止し、印刷CSSで内容を非表示にします。ただしブラウザーへデータを送るため、開発者ツールによる取得、ブラウザー固有機能、キャッシュ、画面キャプチャー、外部撮影を完全には防止できません。

## 外部依存関係とライセンス

サーバーは.NET 8（Microsoftライセンス）、テストはxUnit（Apache-2.0）を使用します。PDF表示は現行ブラウザー内蔵PDFレンダラーをツールバー非表示で利用し、PDF.jsやCDN配信物は同梱していません。

詳細な責務境界は [docs/architecture.md](docs/architecture.md) を参照してください。
