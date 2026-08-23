using DriveUnion.Core.Abstractions;

namespace DriveUnion.Infrastructure.Services;

/// <summary>
/// No account in the pool can take this file — disconnected, paused, or out of room.
///
/// <see cref="Core.Application.IUploadCoordinator.BeginAsync"/> returns a non-nullable result, so a
/// refusal has to be an exception. It derives from <see cref="DriveApiException"/> so that a handler
/// which already catches Drive trouble catches this too, and it is separate from
/// <see cref="DriveAccountUnavailableException"/> because a full pool is not a broken credential and
/// the operator's next move is different.
/// </summary>
public sealed class UploadRejectedException(string message) : DriveApiException(message);
