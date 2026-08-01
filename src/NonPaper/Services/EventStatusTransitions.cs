namespace NonPaper.Services;

public static class EventStatusTransitions
{
    public static bool CanTransition(string current, string next) => (current, next) switch
    {
        ("draft", "published") => true,
        ("draft", "closed") => true,
        ("published", "draft") => true,
        ("published", "closed") => true,
        _ => false
    };

    public static string? Validate(string current, string next)
    {
        if (current == next) return "会議は既に指定された状態です。";
        if (current == "closed") return "終了した会議の状態は変更できません。";
        return CanTransition(current, next) ? null : "この状態へは変更できません。";
    }
}

public sealed class EventStateConflictException(string message) : Exception(message);
