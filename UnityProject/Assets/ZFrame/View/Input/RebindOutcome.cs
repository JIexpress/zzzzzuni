namespace ZFrame.View
{
    /// <summary>
    /// Terminal state of one <c>RebindService.Rebind</c> call
    /// (TASK-A-M3-03), reported through the rebind completion callback:
    /// - <see cref="Done"/>: a new binding was captured, passed the conflict
    ///   check and was persisted.
    /// - <see cref="Conflict"/>: the captured key is already used by another
    ///   binding of the same action map — the override was rolled back and
    ///   the conflicting <c>BindingSnapshot</c> is passed to the callback.
    /// - <see cref="Cancelled"/>: the user aborted (Esc, per
    ///   WithCancelingThrough) — nothing changed.
    /// - <see cref="Failed"/>: the rebind could not run (EditMode/no binding/
    ///   another rebind already in progress).
    /// </summary>
    public enum RebindOutcome
    {
        /// <summary>New binding applied and persisted.</summary>
        Done = 0,

        /// <summary>Key already bound in the same action map; override rolled back.</summary>
        Conflict = 1,

        /// <summary>User cancelled; nothing changed.</summary>
        Cancelled = 2,

        /// <summary>Rebind could not start or complete.</summary>
        Failed = 3,
    }
}
