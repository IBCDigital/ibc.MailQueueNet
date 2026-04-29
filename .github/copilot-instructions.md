# Copilot Instructions — Blazor + StyleCop + MudBlazor v8

**Purpose.** These instructions tell AI coding assistants how to write code for this repository. Everything they generate **must** compile cleanly with **StyleCop Analysers** and be **compatible with MudBlazor v8**.

**Language.** Use **Australian English** in comments, XML docs, and user‑facing strings.

**Minimal Warnings.** Always review build output and fix warnings that are easy to fix and don't require major code rewrites.

---

## Repository documentation rules

- This repository has three documentation surfaces:
  - docs-site/docs = authored documentation
  - docfx = API documentation build config and landing pages
  - C# XML comments = canonical API descriptions in code

- When changing public behavior, endpoints, configuration keys, background jobs, integrations, DI registrations, component parameters, or operator workflows, update documentation in the same change.

- Prefer updating existing docs over creating duplicate pages.

- Keep instructions concrete and version-aware.

## 0) Repository‑wide conventions (strict)

1. **Razor components:** Use **code‑behind** pattern (paired `.razor` + `.razor.cs`). **No single‑file components.** Logic lives in the `.razor.cs` partial; keep markup lean.
2. **One type per file:** **Every class/record/struct/interface/enum lives in its own `.cs` file.**
3. **StyleCop file header (SA1633):** **Every `.cs` and `.razor.cs` file includes a valid file header** matching our `stylecop.json` `fileHeaderTemplate`. See template below.
4. **XML documentation:** **All classes, methods, properties, type parameters, and parameters are fully documented** with XML comments that explain purpose, behaviour, and usage. Include `<summary>`, all `<param>` tags, `<typeparam>` for generics, and `<returns>` where applicable. The tone should assist future maintainers and serve as living documentation.
5. **Using directives placement (SA1200 configured “inside namespace”):** **All `using` directives appear *inside* the namespace block** in every file. To support this, **use block‑scoped namespaces** (i.e., `namespace X { ... }`), not file‑scoped namespaces.
6. **Member ordering:** Keep a consistent order everywhere:  
   **Constants → Static fields → Instance fields → Constructors → Delegates/Events → Properties/Indexers → Methods → Nested types.**  
   Within each group: **public → internal → protected → private** and **static before instance**.
7. **Braces and lines:** Always use braces, one statement per line, no single‑line blocks.
8. **Naming:** Private fields are **camelCase** with **no leading underscore**; constants are **PascalCase**.
9. **`this.` prefix:** **Always prefix instance member access** with `this.`.

---

## 1) Non‑negotiable StyleCop rules (avoid these violations)

- **SA1101 — Prefix local calls with `this`:** Always prefix instance fields, properties, and methods with `this.`
- **SA1501 / SA1107 — No single‑line blocks / multiple statements per line:** One statement per line; blocks never share a line.
- **SA1503 — Braces must not be omitted:** Always include `{ }` for `if/else/for/while/using` etc., even for single statements.
- **SA1309 — Field names must not start with `_`:** Private fields use **camelCase** with **no leading underscore**.
- **SA1201 / SA1202 / SA1204 — Member ordering:**  
  1) **public → internal → protected → private**;  
  2) **static before instance**;  
  3) **constants → fields → constructors → delegates/events → properties/indexers → methods → nested types**.  
  A field must not follow a property; a property must not follow a method.
- **SA1134 — One attribute per line:** e.g., `[Parameter]` and `[EditorRequired]` on separate lines.
- **SA1513 / SA1516 — Blank lines:** Add a blank line after a closing brace of a block; separate members with a blank line.
- **SA1633 — File must have header:** Include the repository’s standard file header at the very top of each file.
- **SA1200 — Using directives must be placed within the namespace:** Place `using` directives *inside* the namespace block.

> **Documentation requirement:** All generated members must include XML docs that are accurate, concise, and helpful to future maintainers.

---

## 2) C# & Razor defaults for this repository

**Namespaces & `using` placement**

- **Use block‑scoped namespaces** so that `using` directives can be written **inside** the namespace (to satisfy SA1200).  
- Place all `using` directives **inside** the namespace.

**General C# style**

- `nullable` is enabled; prefer non‑nullable by default and use `?` deliberately.
- Prefer `readonly` for fields and `record` for immutable aggregates where suitable.
- **Always prefix instance access with `this.`** (SA1101).
- Avoid abbreviations; choose descriptive identifiers.
- Do not chain statements; **braces everywhere** even for single statements.

**Attributes**

- One attribute per line (SA1134).

**Spacing and blank lines**

- Blank line **after** a block’s closing brace (SA1513).
- Blank line **between** members (SA1516).

**XML documentation**

- Every public/protected—and where the code benefits, private/internal—symbol has `///` XML with `<summary>`.
- Methods include **all** `<param>` and `<returns>` (and `<typeparam>` if generic).
- Comments should provide meaningful context and intent, not just restate names.

---

## 3) Blazor component guidance

- **Code‑behind pattern only:** Place logic in `.razor.cs` partial classes. Keep `.razor` markup minimal and declarative.
- Avoid `async void` except where the Blazor event pattern requires it; prefer `Task`/`Task<T>`.
- Lifecycle methods: Assume `OnParametersSet{Async}` can run **multiple times**; write idempotent logic and guard expensive work.
- Two‑way binding: use the standard `Value`/`ValueChanged`/`ValueExpression` triplet when creating form‑like components.

---

## 4) MudBlazor **v8** compatibility

- Generate code that targets **MudBlazor v8** APIs. **Do not** use parameters or components that were deprecated or removed prior to v8.
- If you’re unsure about a parameter name, prefer the current MudBlazor documentation and IntelliSense over legacy blog posts or examples.
- Avoid unknown or misspelled Razor attributes: fix unknown‑parameter warnings rather than relying on attribute splatting.
- Keep styling to supported parameters and CSS classes rather than removed/legacy parameters.

---

## 5) File header template (SA1633)

Place this **at the very top** of every `.cs` and `.razor.cs` file and ensure it matches the repository’s `stylecop.json` `fileHeaderTemplate` (update `company`, etc. as needed):

```csharp
//-----------------------------------------------------------------------
// <copyright file="<<FileName>>" company="<<Company>>">
//   Copyright (c) <<Company>>. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
```

> If your `stylecop.json` uses a different template, **follow that template exactly** to satisfy SA1633.

---

## 6) Examples (StyleCop‑clean + code‑behind)

### 6.1 Component code‑behind — header, using‑inside‑namespace, ordering, `this.`, braces, XML docs

```csharp
//-----------------------------------------------------------------------
// <copyright file="CounterCard.razor.cs" company="Your Organisation">
//   Copyright (c) Your Organisation. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace Project.Components
{
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Components;
    using MudBlazor;

    /// <summary>
    /// Displays a count with increment and decrement actions.
    /// </summary>
    public sealed partial class CounterCard : ComponentBase
    {
        /// <summary>
        /// The default increment applied when increasing the count.
        /// </summary>
        private const int DefaultIncrement = 1;

        /// <summary>
        /// A static default variant for action buttons.
        /// </summary>
        private static readonly Variant ButtonVariant = Variant.Filled;

        /// <summary>
        /// Holds the current count value for this component instance.
        /// </summary>
        private int count;

        /// <summary>
        /// Gets or sets the starting count value applied when the component initialises or is reset.
        /// </summary>
        [Parameter]
        public int Start { get; set; }

        /// <summary>
        /// Gets or sets the increment step applied when changing the count.
        /// </summary>
        [Parameter]
        public int Step { get; set; } = DefaultIncrement;

        /// <summary>
        /// Resets the counter to <see cref="Start"/>.
        /// </summary>
        public void Reset()
        {
            this.count = this.Start;
        }

        /// <summary>
        /// Increments the current count.
        /// </summary>
        /// <returns>A completed <see cref="Task"/>.</returns>
        public Task IncrementAsync()
        {
            this.count += this.Step;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Decrements the current count.
        /// </summary>
        /// <returns>A completed <see cref="Task"/>.</returns>
        public Task DecrementAsync()
        {
            this.count -= this.Step;
            return Task.CompletedTask;
        }
    }
}
```

### 6.2 Component markup — attributes on their own lines; minimal logic in markup

```razor
@* File: Components/CounterCard.razor *@
<MudPaper Class="pa-4">
    <MudText Typo="Typo.h6">Count: @this.count</MudText>

    <MudStack Row="true" Spacing="2">
        <MudButton
            Variant="@ButtonVariant"
            Color="Color.Primary"
            OnClick="this.IncrementAsync">
            Increase
        </MudButton>

        <MudButton
            Variant="@ButtonVariant"
            Color="Color.Secondary"
            OnClick="this.DecrementAsync">
            Decrease
        </MudButton>

        <MudButton
            Variant="Variant.Outlined"
            Color="Color.Default"
            OnClick="this.Reset">
            Reset
        </MudButton>
    </MudStack>
</MudPaper>

@code {
    // Intentionally empty: logic lives in the .razor.cs partial.
}
```

---

## 7) Quick fix patterns for common warnings

- **SA1101** — Prefix with `this.`  
  ```csharp
  this.logger.LogInformation("Started");
  this.count++;
  ```

- **SA1501 / SA1107** — Expand to multiple lines.  
  ❌ `if (ready) DoWork(); DoMore();`  
  ✅
  ```csharp
  if (ready)
  {
      this.DoWork();
  }

  this.DoMore();
  ```

- **SA1503** — Always include braces.  
  ❌ `if (isValid) return;`  
  ✅
  ```csharp
  if (isValid)
  {
      return;
  }
  ```

- **SA1309** — Rename fields; no leading underscore.  
  ❌ `private int _count;` → ✅ `private int count;`

- **SA1201 / SA1202 / SA1204** — Reorder members as per section 0.6 above.

- **SA1134** — One attribute per line.  
  ```csharp
  [Parameter]
  [EditorRequired]
  public string Name { get; set; } = string.Empty;
  ```

- **SA1513 / SA1516** — Maintain blank lines after blocks and between members.

- **SA1633** — Add the file header (see template above).

- **SA1200** — Ensure `using` directives live **inside** the namespace.

---

## 8) Pre‑commit checklist for generated/edited code

- ✅ **Code‑behind only** for Razor components (paired `.razor` + `.razor.cs`).  
- ✅ **One type per file.**  
- ✅ **Valid StyleCop file header** present (SA1633).  
- ✅ **All members fully XML‑documented** with `<summary>`, `<param>`, `<typeparam>`, `<returns>` where applicable.  
- ✅ **Using directives inside the namespace** (SA1200).  
- ✅ No **SA1101**, **SA1501**, **SA1107**, **SA1503**, **SA1309**, **SA1201**, **SA1202**, **SA1204**, **SA1134**, **SA1513**, **SA1516** warnings.  
- ✅ MudBlazor usage is **v8‑compatible** (no legacy/removed parameters).  
- ✅ Lifecycle code is **idempotent** across `OnParametersSet{Async}` reruns; heavy work guarded.

---

## 9) Optional `stylecop.json` helpers

To enforce some of the above automatically, consider these settings in your `stylecop.json` at the repository root:

```json
{
  "settings": {
    "documentationRules": {
      "documentExposedElements": true,
      "documentInterfaces": true,
      "documentInternalElements": true,
      "documentPrivateElements": true,
      "documentPrivateFields": true,
      "companyName": "Your Organisation",
      "fileHeaderTemplate": "-----------------------------------------------------------------------\n<copyright file=\"{fileName}\" company=\"{companyName}\">\n  Copyright (c) {year} {companyName}. All rights reserved.\n</copyright>\n-----------------------------------------------------------------------"
    },
    "orderingRules": {
      "usingDirectivesPlacement": "insideNamespace"
    }
  }
}
```

> Adjust `companyName`, `fileHeaderTemplate`, and documentation scope to match your project’s conventions.

---

## 10) Mail Routing Allow-list Feature

- For the staging-only mail-routing allow-list feature, clients should manage their own lists using **client shared-secret authentication** rather than **admin authentication**. However, MailFunk should also be able to manage any client's list using admin credentials.
- Real SMTP deliveries should include a **subject marker**.
