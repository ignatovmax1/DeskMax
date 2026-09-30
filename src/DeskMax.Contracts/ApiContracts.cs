namespace DeskMax.Contracts;
public record RegisterDeviceRequest(string DeviceName, string Platform);
public record RegisterDeviceResponse(string DeviceId, string DeviceSecret, DateTimeOffset RegisteredAt);
public record RotatingCodeResponse(string DeviceId, string Code, DateTimeOffset ExpiresAt);
public record CreateSessionRequest(string TargetCode, string RequesterDeviceId);
public record SessionResponse(Guid SessionId, string Status, string TargetDeviceId, DateTimeOffset ExpiresAt);
public record ApproveSessionRequest(string DeviceSecret);
public record GrantAccessRequest(string OwnerDeviceSecret, string TrustedDeviceId, DateTimeOffset? ExpiresAt);
