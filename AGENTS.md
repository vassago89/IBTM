# Verification workflow

The user performs physical-equipment testing and prefers short edit/verify cycles.

- For UI/layout/text-only changes, skip tests; compile only when needed. For motion/I/O/control changes, run only the directly affected safety/regression tests, in one configuration.
- `dotnet test` builds its dependencies. Do not also build the whole solution or repeat the same tests in Debug and Release without a concrete reason.
- Run the full virtual route/lifecycle suite only when the user explicitly asks. Do not run it after every UI or driver-wrapper edit.
- Retain safety, error-handling and regression coverage. Remove obsolete expectations and redundant parameter combinations rather than deleting useful tests merely because they take time.
- Do not launch the physical application or invoke native hardware APIs for verification unless the user explicitly asks. Use SDK stand-ins or the Virtual configuration.
