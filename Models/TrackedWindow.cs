namespace QiDock.Models;

public sealed record TrackedWindow(nint Handle, string Title, string? ExecutablePath, bool HasNotification = false);
