---
title: Handlebars (Handlebars.Net) syntax reference
sidebar_position: 9
---

This page is a quick, practical reference for writing **Handlebars** templates in the MailQueueNet stack.

In this repository, Handlebars templates are rendered by **Handlebars.Net** (via MailForge). Handlebars is expression-light by design: it focuses on property lookups plus a small set of helpers/blocks.

## When to choose Handlebars

Handlebars is a good fit when you want:

- Very simple templates that mostly do **field substitution**.
- A syntax that is familiar to teams coming from JavaScript ecosystems.
- Minimal “logic in templates” (by convention).

## Strengths

- Simple and readable for plain substitution.
- Good for non-technical users when templates remain simple.
- Supports common block patterns such as `#if` and `#each`.

## Weaknesses / limitations

- Complex transformations typically require helpers (and helper availability depends on the host configuration).
- Debugging can be harder when templates rely on implicit truthiness rules.
- Triple-stash (`{{{ ... }}}`) changes escaping behaviour and can introduce HTML injection risks if used carelessly.

## Data model: how fields are referenced

Each merge row is a JSON object (one line in a JSONL batch). MailForge requires the recipient field:

- `Email` (case-insensitive)

Example JSON row:

```json
{"Email":"alice@example.com","FirstName":"Alice","LastName":"Ng","Account":{"Id":123,"Plan":"Gold"}}
```

### Example field references

- Top-level: `{{FirstName}}`
- Nested object: `{{Account.Plan}}`

## Basic syntax

### Variable interpolation

```handlebars
Hello {{FirstName}} {{LastName}}
```

### Conditionals

```handlebars
{{#if Account.Plan}}
Your plan is: {{Account.Plan}}
{{else}}
No plan is set.
{{/if}}
```

### Loops (arrays)

If your JSON row contains an array:

```json
{"Email":"alice@example.com","Items":[{"Name":"Widget","Qty":2},{"Name":"Cable","Qty":1}]}
```

You can loop:

```handlebars
{{#each Items}}
- {{Name}} (x{{Qty}})
{{/each}}
```

## Practical mail-merge example

Subject:

```handlebars
Welcome {{FirstName}}
```

Body:

```handlebars
Hi {{FirstName}},

Your plan is: {{Account.Plan}}.

If you need help, reply to this email.
```

## Notes for operators

- Handlebars vs Liquid selection is controlled by the merge template header `X-MailMerge-Engine`.
- A best-effort syntax converter exists for simple `{{ Variable }}` patterns; control blocks and helpers generally cannot be converted safely.
