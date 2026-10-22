# SharpEngine code conventions

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
  repository-wide renames. Keep third-party code under `External/` in its own style.

The root `.editorconfig` supplies the corresponding C# naming suggestions.
`Templates/BasicProject/AGENTS.md` and `.editorconfig` carry these defaults into
new projects; keep their naming policy aligned with this one.

For engine development and project-side engine extensions, follow
[sharpengine-development](.agents/skills/sharpengine-development/SKILL.md).
Feature boundaries and nesting follow the
[composition architecture](Documentation/CompositionArchitecture.md): acyclic
dependencies, encapsulated capabilities, reusable definitions with per-use data,
and domain-typed composition slots. Apply
[engine-code-style](.agents/skills/engine-code-style/SKILL.md) to changed engine
source and code examples.
For every observable engine feature or public contract change, also follow
[engine-manual](.agents/skills/engine-manual/SKILL.md) and update the current
versioned HTML manual under `Documentation/EngineManual`.
