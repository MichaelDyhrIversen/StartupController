namespace StartupController
{
    internal enum StartupAction
    {
        Normal,              // open the app (window or tray); nothing is launched automatically
        LaunchAndExit,       // --launch: launch the list, then exit
        ExitAlreadyLaunched, // --launch again in the same logon session: exit at once, no UI and no signal
        BlockedAndExit       // --launch, but the logon session couldn't be recorded: launch nothing, notify, exit
    }

    // How Program.Main starts (4.D8). Pure apart from calling claim and logging. claim runs only for --launch with
    // "Launch programs on startup" on, so no other start ever reads or writes a LaunchSession value.
    internal static class LaunchGate
    {
        internal static StartupAction Decide(bool hasLaunchArg, bool launchSettingOn, Func<LaunchClaim> claim)
        {
            ArgumentNullException.ThrowIfNull(claim);
            if (!hasLaunchArg || !launchSettingOn) return StartupAction.Normal;

            LaunchClaim result;
            try
            {
                result = claim();
            }
            catch (Exception ex)
            {
                // LaunchSessionGuard.TryClaim doesn't throw; anything else fails closed like Unavailable
                LoggingService.LogError("--launch: the logon session check failed", ex);
                result = LaunchClaim.Unavailable;
            }

            return result switch
            {
                LaunchClaim.Claimed => StartupAction.LaunchAndExit,
                LaunchClaim.AlreadyLaunched => StartupAction.ExitAlreadyLaunched,
                _ => StartupAction.BlockedAndExit
            };
        }

        // The one mapping from the decision to the form's mode. LaunchMode: launch-and-exit (the window is not shown);
        // LaunchBlocked: launch mode that launches nothing, shows the blocked balloon and exits.
        internal static (bool LaunchMode, bool LaunchBlocked) FormFlagsFor(StartupAction action) =>
            (action is StartupAction.LaunchAndExit or StartupAction.BlockedAndExit, action == StartupAction.BlockedAndExit);
    }
}
