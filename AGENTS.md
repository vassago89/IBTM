# Development priority

- Optimize for a short implement / equipment-check / fix cycle. Readability and structure serve development speed, not architectural completeness.
- Keep device calls and sequence steps explicit. Allow small local repetition when a new abstraction would make debugging or changes slower.
- Simplify code in the affected path; do not add speculative frameworks, duplicate state or repeated cleanup passes without a concrete benefit.
- Refactoring should remove duplicated work, storage and forwarding layers. Do not add helper classes, generic dispatchers or new packages merely to shorten individual methods. Keep device implementations aligned with the supplied originals.

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
- For stored UI values that need binding change notifications, use `[ObservableProperty] public partial T Name { get; set; }` instead of annotated backing fields or handwritten notification boilerplate. Preserve custom accessors for live state or forwarding into settings, and keep commands explicitly declared.
- Do not poll display state with UI-thread timers. Expose state changes through observable properties/collections and bind the view to them; keep device acquisition loops outside the UI.

# Debuggable control flow

- Use file-scoped namespaces (`namespace X;`). Follow `C:\git\MWD100` for ordinary `switch` statements and explicit sequential device calls. Keep the project's named, explicitly declared commands.
- Keep command handling and configuration writes in named methods that F12 can follow. Avoid constructor callbacks or generic dispatchers that only forward to known concrete objects.
- Initialize commands, owned objects, collections and factory-created member values in constructors, keeping declarations separate from construction. Use static constructors for static members and the existing constructor for partial classes. Preserve dependency order; initialize observable properties only after the objects and commands used by their change hooks exist.
- Remove one-use forwarding helpers and redundant axis-specific wrappers when the actual implementation already takes an axis. Preserve hardware boundaries, cancellation lifetimes, feedback handling and real interlocks.
- Use ordinary block bodies for methods and local functions. Use `=>` for single-line computed properties and get/set accessors; keep blocks for multiple statements or multiline expressions. Keep simple auto-properties and ordinary lambda/switch syntax.
- Use auto-properties for simple value storage instead of separate backing fields. Preserve existing write access (`get;`, `private set;`, or `get; set;`); keep custom accessors and fields when they provide behavior, synchronization, or a narrower public view of mutable data.
- For custom accessors that alone use their backing field, use C# `field` and an automatic getter instead of declaring a separate field. Preserve defaults, setter side effects and notification order; retain explicit fields for shared access, `volatile` and synchronization.
- Expose current state through properties instead of parameterless `Get...()` methods. Put fixed sensor-pair state checks directly in the relevant property; keep methods for operations and queries whose caller supplies meaningful arguments.
- Put simple value assignment and its existing change notifications in property setters instead of `Set...` wrappers. Preserve setter visibility and notification behavior; keep device commands, asynchronous work and multi-argument operations as methods.
- Use `Is...` for boolean checks, with `Is...Allowed` for permission to perform an action. Use properties for parameterless checks; keep meaningful target/feedback arguments on methods. Command predicates must evaluate these properties when invoked, for example `() => IsHomeAllowed`.
- Break complex conditions at logical operators and long calls between arguments so each meaningful part is easy to scan. Do not compress code to minimize line count, or expand simple readable branches merely to add lines.
- Chain consecutive DI registrations that return `IServiceCollection`, with one registration per line. Separate logical groups with blank lines and preserve registration order; keep `void` registration APIs such as `TryAddSingleton` as separate statements.
- Prefer constructor injection with `AddSingleton<T>()` or `AddSingleton<I, T>()` when dependencies are registered. Keep factories only for actual composition such as keyed devices or virtual feedback wiring. Inject `RecipeManager` for recipe access and read its `Current` property; do not register recipe data or recipe getter delegates as dependencies.
- Use type inference, null operators and pattern matching where they remove repetition without hiding the operation. Combine short `case` labels with `or` when they share a body; preserve branch priority and live-feedback read order. Keep meaningful intermediate values used during debugging.
- Use `await` through asynchronous call paths, not `.Wait()`, `.Result` or `GetAwaiter().GetResult()`. Use `Task.Run` only for actual synchronous device/CPU work that must leave the UI thread, not to wrap an already asynchronous operation.
- Use types/enums for application decisions and `nameof` for member references instead of string dispatch. Report unhandled application errors to the operator as well as the log, keeping cancellation and normal waiting separate from failures.

# Physical equipment state

- Hardware can change outside this application. Determine position, movement, readiness and actuator completion from current I/O or SDK feedback, not from a previous command or initialization flag.
- Keep command ownership, cancellation, selected destinations and uncollected results as software history only; do not present them as physical state or use them to override contradictory feedback.
- Missing feedback is unknown, not OFF, position zero, ready or completed. Expose unhandled/ambiguous states explicitly; do not silently retry an assumed sequence or hide a skipped operation as success.
- Preserve actual interlocks and result ownership. Do not delete necessary history when sensors cannot distinguish intermediate positions; report the uncertainty instead of guessing.
