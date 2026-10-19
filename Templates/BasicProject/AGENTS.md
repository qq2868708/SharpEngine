# Project code conventions

Use PascalCase (UpperCamelCase) for new or edited C# identifiers, regardless of
visibility: namespaces, types, methods, properties, events, fields (including
private, static, readonly and const fields), parameters, local variables, local
functions and lambda parameters.

- Use `MoveSpeed`, `Context`, `Entity` and `Index`; private fields use
  `MainScreenContract`, without `_`, `m_` or `s_` prefixes.
- Use `this.MoveSpeed = MoveSpeed` when a parameter shadows an instance member.
- Keep conventional interface/type parameter names such as `IPlayable` and `TState`.
  The discard `_` is allowed.
- Preserve names required by overrides, external APIs, named arguments and existing
  serialized contracts (JSON keys, asset IDs, component/settings field IDs, etc.).
  Code naming does not change those contracts.
- Apply this convention to new code and the code being changed; avoid unrelated
  project-wide renames. Keep third-party code in its own style.

The root `.editorconfig` supplies the corresponding C# naming suggestions.
These defaults apply to project Modules and project-owned Plugins. Change this
file and `.editorconfig` together when choosing a different project convention.

Runtime Modules reference only SharpEngine.Runtime; Editor Modules may also
reference SharpEngine.Editor. Use the engine's unified project build entry point
documented in README.md. Keep serialized IDs and schema versions stable.
