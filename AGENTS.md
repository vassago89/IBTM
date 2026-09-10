# Verification workflow

The user performs physical-equipment testing and prefers short edit/verify cycles.

- For UI/layout/text-only changes, skip tests; compile only when needed. For motion/I/O/control changes, run only the directly affected safety/regression tests, in one configuration.
- `dotnet test` builds its dependencies. Do not also build the whole solution or repeat the same tests in Debug and Release without a concrete reason.
- Run the full virtual route/lifecycle suite only when the user explicitly asks. Do not run it after every UI or driver-wrapper edit.
- Retain safety, error-handling and regression coverage. Remove obsolete expectations and redundant parameter combinations rather than deleting useful tests merely because they take time.
- Do not launch the physical application or invoke native hardware APIs for verification unless the user explicitly asks. Use SDK stand-ins or the Virtual configuration.
- Keep tests proportional to the current product: do not add layout/color snapshots, exhaustive combinations of independent inputs, or repeat a full virtual route when a focused behaviour test already covers it. Delete superseded tests and unused setup code.
- Long route simulations use `Category=MachineFlow` and are opt-in. Prefer the named affected tests; do not compensate for removing duplicate tests by running all remaining tests after every edit.

# XAML binding structure

- Declare the actual binding-source type at each View/Window root with `d:DataContext="{d:DesignInstance Type=..., IsDesignTimeCreatable=False}"` for editor navigation and F12. This is design-time metadata, not a runtime DataContext assignment.
- Do not assign or traverse DataContext inside nested XAML elements, and do not use RelativeSource. Keep runtime context ownership explicit in C# and let child views/templates inherit their context.
- Bind commands and values explicitly where they are used. Use DataTemplate for repeated or dynamic presentation; do not hide application bindings behind ContentControl wrappers.

# Debuggable control flow

- Keep command handling and configuration writes in named methods that F12 can follow. Avoid constructor callbacks or generic dispatchers that only forward to known concrete objects.
- Remove one-use forwarding helpers and redundant axis-specific wrappers when the actual implementation already takes an axis. Preserve hardware boundaries, cancellation lifetimes, feedback handling and real interlocks.
