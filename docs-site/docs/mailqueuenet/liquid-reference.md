---
title: Liquid (Fluid.Core) syntax reference
sidebar_position: 8
---

This page is a quick, practical reference for writing **Liquid-style** templates in the MailQueueNet stack.

In this repository, Liquid templates are rendered by **Fluid.Core** (via MailForge). The syntax is broadly compatible with Liquid, but the exact feature set depends on Fluid and how the host application configures it.

## When to choose Liquid

Liquid is a good fit when you want:

- A familiar template language with strong support for **conditionals**, **loops**, and **filters**.
- Reasonably readable templates for non-developers.
- Safer-by-default output behaviour for HTML (depending on how you render/escape).

## Strengths

- Strong control-flow support: `if`, `unless`, `for`.
- Filter syntax for common transformations (for example string casing, date formatting) when enabled.
- Widely-known syntax and lots of examples.

## Weaknesses / limitations

- Filter availability depends on the engine configuration (Fluid can be configured with a limited set).
- Complex templates can become hard to debug without good test data.
- MailForge currently treats each JSON row as the model; missing properties generally render as blank, which can hide data issues.

## Data model: how fields are referenced

Each merge row is a JSON object (one line in a JSONL batch). MailForge requires the recipient field:

- `Email` (case-insensitive)

Example JSON row:

```json
{"Email":"alice@example.com","FirstName":"Alice","LastName":"Ng","Account":{"Id":123,"Plan":"Gold"}}
```

### Example field references

- Top-level: `{{ FirstName }}`
- Nested object: `{{ Account.Plan }}`

## Basic syntax

### Variable interpolation

```liquid
Hello {{ FirstName }} {{ LastName }}
```

### Conditionals

```liquid
{% if Account.Plan == "Gold" %}
  Thanks for being a Gold customer.
{% else %}
  Thanks for being a customer.
{% endif %}
```

### Loops (arrays)

If your JSON row contains an array:

```json
{"Email":"alice@example.com","Items":[{"Name":"Widget","Qty":2},{"Name":"Cable","Qty":1}]}
```

You can loop:

```liquid
{% for item in Items %}
- {{ item.Name }} (x{{ item.Qty }})
{% endfor %}
```

## Practical mail-merge example

Subject:

```liquid
Welcome {{ FirstName }}
```

Body:

```liquid
Hi {{ FirstName }},

Your plan is: {{ Account.Plan }}.

If you need help, reply to this email.
```

## Notes for operators

- Liquid vs Handlebars selection is controlled by the merge template header `X-MailMerge-Engine`.
- If you are migrating templates between engines, the repository includes a best-effort converter for *simple* `{{ Variable }}` patterns, but advanced expressions are not guaranteed to convert.
