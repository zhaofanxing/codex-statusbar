namespace CodexStatusbar;

internal enum OverlayLanguage
{
    Chinese = 0,
    English = 1
}

/// <summary>Display language is independent of the OS culture and metric formatting.</summary>
internal static class OverlayText
{
    public static string Choose(OverlayLanguage language, string chinese, string english) =>
        language == OverlayLanguage.English ? english : chinese;

    public static OverlayLanguage ParseLanguage(string? value) =>
        string.Equals(value, "en", StringComparison.OrdinalIgnoreCase)
            ? OverlayLanguage.English
            : OverlayLanguage.Chinese;

    public static string LanguageName(OverlayLanguage language) =>
        language == OverlayLanguage.English ? "en" : "zh-CN";

    public static string AnchorName(OverlayLanguage language, AttachmentReferencePoint anchor) => anchor switch
    {
        AttachmentReferencePoint.TopLeft => Choose(language, "左上", "Top left"),
        AttachmentReferencePoint.TopCenter => Choose(language, "顶部居中", "Top center"),
        AttachmentReferencePoint.TopRight => Choose(language, "右上", "Top right"),
        AttachmentReferencePoint.LeftCenter => Choose(language, "左侧居中", "Left center"),
        AttachmentReferencePoint.Center => Choose(language, "居中", "Center"),
        AttachmentReferencePoint.RightCenter => Choose(language, "右侧居中", "Right center"),
        AttachmentReferencePoint.BottomLeft => Choose(language, "左下", "Bottom left"),
        AttachmentReferencePoint.BottomCenter => Choose(language, "底部居中", "Bottom center"),
        AttachmentReferencePoint.BottomRight => Choose(language, "右下", "Bottom right"),
        _ => anchor.ToString()
    };

    public static string LifecycleState(OverlayLanguage language, CodexLifecycleState state) => state switch
    {
        CodexLifecycleState.WaitingForCodex => Choose(language, "等待 Codex", "Waiting for Codex"),
        CodexLifecycleState.Attaching => Choose(language, "连接中", "Connecting"),
        CodexLifecycleState.Active => Choose(language, "运行中", "Active"),
        _ => Choose(language, "断开中", "Detaching")
    };
}
