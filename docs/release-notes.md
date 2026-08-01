# Release Notes

## v0.1.0

初回リリースです。庁内ネットワークで完結する、ブラウザー型の簡易ペーパーレス会議システムを提供します。

### 主な機能

* イベント（会議）の作成、公開、下書きへの差し戻し、終了、削除
* 複数PDF資料の登録、名称・順序の変更
* 参加者向けの資料閲覧画面（資料切替、ページ移動、拡大縮小、全画面表示）
* 会議終了後、参加者画面が最大30秒以内に閲覧を終了
* イベント削除時にJSON・PDF・イベントフォルダーを一括削除

### 動作環境

* Windows Server/Windows、IISまたはASP.NET Core Kestrel
* .NET 8 SDK（配置先にはASP.NET Core Hosting Bundleまたは.NET 8 Runtime）
* 現行版のChrome、Edge、Firefox

### セキュリティ

* イベント/資料IDは128ビット乱数、管理トークンは256ビット乱数
* 管理トークンは作成時のみ表示し、保存はSHA-256ハッシュのみ
* PDFは静的領域に置かず、公開状態と所属を確認するRange対応APIから配信
* 通常の画面操作ではPDFのダウンロード・印刷を提供しない

### 制約

* SPA、データベース、外部API、CDNは使用しません
* `closed` は終端状態であり、終了した会議は再公開できません

詳細は [README.md](../README.md) および [docs/architecture.md](architecture.md) を参照してください。
