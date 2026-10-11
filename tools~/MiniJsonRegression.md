# MiniJson EOF regression

From the repository root, with Python and an existing .NET SDK:

```text
python tools~/run_mini_json_regressions.py --source Editor/MiniJson.cs
```

The runner compiles the actual source as C# 9 against existing netstandard 2.1 references. It creates a temporary executable, runs 36 named checks plus 50 selected prefix checks, and returns nonzero on an unexpected result. It performs no package restore, network access or source modification. Use `--dotnet`, `--compiler` and `--reference` to select existing bundled tools explicitly.

Empty input must reach the parser's existing EOF result (`null`). Valid scalar types, nested session-shaped data, escapes and Unicode retain their results. The original source fails 47 checks; `--expect-failures 47` can qualify that baseline. The EOF guard passes all 86 checks.

Several named cases preserve existing permissive parsing behavior. In particular, incomplete Unicode escapes still throw, unterminated strings can return partial strings, and trailing content can be ignored. This patch does not establish strict JSON validation or fix unrelated Unity singleton errors.
