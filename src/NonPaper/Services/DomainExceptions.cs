namespace NonPaper.Services;

/// <summary>入力値が業務ルールを満たさない（HTTP 400）。</summary>
public sealed class RequestValidationException(string message) : Exception(message);

/// <summary>指定された資料がイベントに存在しない（HTTP 404）。</summary>
public sealed class DocumentNotFoundException() : Exception("資料が見つかりません。");

/// <summary>資料の件数またはファイルサイズが上限を超えている（HTTP 413）。</summary>
public sealed class UploadLimitException(string message) : Exception(message);

/// <summary>現在の状態では実行できない操作（HTTP 409）。</summary>
public sealed class EventStateConflictException(string message) : Exception(message);

/// <summary>管理用トークンが一致しない（HTTP 401）。ファイル操作の権限エラーと取り違えないよう専用の型にする。</summary>
public sealed class ManagementTokenException() : UnauthorizedAccessException("管理用URLが正しくありません。");

/// <summary>保存領域のファイルが使用中で操作を完了できない（HTTP 503）。時間をおいた再実行で解消する。</summary>
public sealed class StorageBusyException(string message, Exception? inner = null) : Exception(message, inner);
