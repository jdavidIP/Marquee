namespace Marquee.Domain.Enums;

public enum UserRole
{
    User = 0,
    Admin = 1
}

public enum PremiereStatus
{
    Scheduled = 0,
    Active = 1,
    Opened = 2,
    AutoOpened = 3,
    /// <summary>
    /// Its moment passed without it ever going live — the scheduler was not running when it came
    /// due, and by the time it was, running the Premiere late would have been worse than not
    /// running it (see PremiereScheduleService.ActivateDueAsync).
    ///
    /// Distinct from AutoOpened, which is the §4.5 "no failure state" outcome for a Premiere that
    /// *did* run and simply did not reach its threshold. A Missed Premiere revealed nothing, so its
    /// film was never seen and stays available for a future one.
    /// </summary>
    Missed = 4
}

public enum FriendshipStatus
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2
}
