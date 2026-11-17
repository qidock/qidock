using System.Windows.Media.Imaging;

namespace QiDock.Models;

public enum JumpListStatus { Ready, Empty, IdentityUnavailable, Unsupported, Unavailable }
public sealed record JumpListEntry(int CommandId, string Title, BitmapSource? Icon, bool IsSeparator = false);
public sealed record JumpListGroup(string Title, IReadOnlyList<JumpListEntry> Entries);
public sealed record ApplicationJumpList(long SessionId, JumpListStatus Status, IReadOnlyList<JumpListGroup> Groups);
