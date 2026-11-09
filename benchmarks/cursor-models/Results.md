# Results: Cursor model benchmark

Protocol: [PROTOCOL.md](PROTOCOL.md) (committed before any model was asked). Prompt: [prompt.txt](prompt.txt). Raw answers: [answers/](answers/).

## Summary

| Model | Where it ran | Builds | Planted errors fixed | Lines touched | Error codes named correctly | Time to answer |
|---|---|---|---|---|---|---|
| **Grok 4.7** | Cursor (Ask mode) | ✅ yes | 3/3 | 3 (2 edited, 1 added) | ✅ 3/3 | 33 s |
| **Gemma 3 4B** | Local, Ollama (not a Cursor model) | ✅ yes | 3/3 | 6 (3 edited, 3 added) | ❌ 0/3 | 17 s |

"Builds" means the answer, dropped into a clean copy of `input/` with no `bin/` or `obj/`, built with zero errors on .NET SDK 10.0.403.

## How each model fixed the three errors

| Planted error | Grok 4.7 | Gemma 3 4B |
|---|---|---|
| **CS0029**: `Task<string>` assigned to `string` | `await client.GetStringAsync(...)` | `.Result` |
| **CS1061**: `body.Lenght` | `body.Length` | `body.Length` |
| **CS0161**: `Half` doesn't always return | Added `return 0;` | Rewrote the method around `n % 2` |
| Other changes | None | Added `using System;` (already covered by implicit usings) |

## Pros and cons

### Grok 4.7 (Cursor)
**Pros**
- The most correct fix: `await` is the idiomatic way to get a `string` out of `Task<string>`.
- Smallest change. It followed "change only what is needed."
- Named all three error codes correctly, with short, accurate reasons.

**Cons**
- About twice as slow as the local model (33 s).
- `return 0;` for `n <= 0` is an assumption; the original code never said what `Half` should return there.
- A cloud model: the code leaves the machine.

### Gemma 3 4B (local, for comparison)
**Pros**
- Fastest answer (17 s), fully offline and free.
- The code compiles, and the CS1061 typo fix is correct.

**Cons**
- `.Result` blocks the thread. It compiles, but `await` is the right fix.
- **Changed behavior:** the rewritten `Half` rounds odd numbers up. The original `Half(5)` would return 2; Gemma's returns 3.
- Added an unneeded `using System;`.
- **The explanation was wrong:** it gave no error codes, described a "System.Linq was not used" error that doesn't exist, never mentioned the `Task`/`await` problem, and described CS0161 as "doesn't handle odd numbers."

## Takeaways
1. **"It compiles" is not the same as "it's correct."** Both answers built, but only one preserved the program's behavior and explained the errors accurately.
2. **Build success is necessary but not enough to score a fix.** A behavior check (tests) and a review of the explanation caught what the compiler couldn't.
3. **The baseline mattered.** The first draft of the input used a missing semicolon, which produced a syntax error that hid the other two errors. Recording the baseline before running any model caught it.

## Limitations
- Only one Cursor model was available on my plan; the other planned models stayed locked. Gemma 3 4B is a local comparison, not a Cursor model.
- One run per model (n = 1), on one small file with three planted errors.
- Gemma ran with Ollama's default settings, not temperature 0.
- Times are wall-clock times, measured by hand or from Cursor's display.