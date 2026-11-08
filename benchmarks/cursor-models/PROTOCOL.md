# Cursor model benchmark: protocol

Written and committed **before** any model was asked, so the setup can't be tuned to the results.

## Question
Given the same broken C# file and the same prompt, which Cursor model returns a fix that actually builds, with the fewest unneeded changes?

## Input
- `input/Program.cs`: a .NET console program with three planted errors:
  - **CS0029**: `GetStringAsync` returns `Task<string>`, assigned to `string` (missing `await`)
  - **CS1061**: `body.Lenght`, a typo for `Length`
  - **CS0161**: not all code paths return a value
- `baseline.txt`: the real `dotnet build` output for the input, proving which errors the compiler reports.
- **Revision note:** the first draft used a missing semicolon (CS1002). The baseline showed it caused a syntax error (CS1003) that hid the other two errors, so it was replaced with CS1061 before any model was run.

## Prompt
`prompt.txt`, pasted unchanged. It already contains the input file.

## Procedure (identical for every model)
1. Cursor → **New Chat**, mode set to **Ask** (the model answers but does not edit files).
2. Select the model at the bottom of the chat.
3. Paste `prompt.txt` exactly. Attach nothing else.
4. Note the time to answer (Cursor's "Worked for…" line, or a stopwatch).
5. Copy the model's code block **unedited** into `answers/<model-name>.cs`.
6. Screenshot the chat.

## Scoring (in priority order)
1. **Builds:** the answer, dropped into a copy of `input/`, builds with zero errors.
2. **Original errors fixed:** how many of the baseline errors are gone (0–3).
3. **Lines changed:** lines added + removed versus the input. Fewer is better ("change only what is needed").
4. **Explanation accuracy:** did it name the right error codes?
5. **Fix quality (reviewer note):** for CS0029, `await` is the idiomatic fix; `.Result` or `.GetAwaiter().GetResult()` compiles but blocks the thread.
6. **Time to answer.**

## Environment
Fill in before running:

| Item | Value |
|---|---|
| Date | 2026-11-08 |
| Cursor version (Help → About) | 3.29.9 |
| Cursor plan | Entry level |
| .NET SDK (`dotnet --version`) | 10.0.403 |
| Model 1 | Cursor |
| Model 2 | GPT-5.6 Sol |
| Model 3 | Gemini 4 Argon |

## Deviations from the plan
- **2026-11-07:** At run time only Grok 4.7 was selectable on my Cursor plan. Composer, GPT-5.6 Sol and Gemini 4 Argon showed as locked. Grok 4.7 was run with the unchanged prompt; the others will be run if they unlock before the deadline.

## Limitations
- One run per model (n = 1). Model output varies between runs, so treat this as a snapshot, not a ranking.
- One small, single-file program with known errors. Real projects are messier.
- Model choice is limited to what the Cursor plan offers.