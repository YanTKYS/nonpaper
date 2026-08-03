namespace NonPaper.Services;

/// <summary>入力値が業務ルールを満たさない（HTTP 400）。</summary>
public sealed class RequestValidationException(string message) : Exception(message);

/// <summary>指定された資料がイベントに存在しない（HTTP 404）。</summary>
public sealed class DocumentNotFoundException() : Exception("資料が見つかりません。");

/// <summary>資料の件数またはファイルサイズが上限を超えている（HTTP 413）。</summary>
public sealed class UploadLimitException(string message) : Exception(message);

/// <summary>現在の状態では実行できない操作（HTTP 409）。</summary>
public sealed class EventStateConflictException(string message) : Exception(message);
