# Feature Cards

Use one card per durable feature or domain. Feature cards are the only hand-maintained Atlas routing layer.

Keep `## Task keywords`, `## Important files`, and `## Nearest tests` concise. `route.ps1` reads those sections deterministically, and repeated prompt-cached Atlas prefixes may reuse the cards verbatim.

```markdown
# <Feature or Domain>

## Purpose

## Task keywords

## Entrypoints

## Important symbols

## Important files

## Nearest tests

## Build/test commands

## Invariants

## Common pitfalls

## Do not read first

## Last verified
```
