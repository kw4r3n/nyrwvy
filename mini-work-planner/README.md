# Mini Work Planner

Nyrwvy の maintained AuthoringSession で作成・保守した、単一 work item の次アクション判定ツールです。

semantic source は `planner.snapshot.json` です。CLI 側は snapshot を直接解釈せず、公開 `declarations` と `emitCSharp.symbols` から生成された C# を呼ぶだけの adapter です。

## 判定モデル

入力:

- `PrerequisiteState = Ready | Waiting | Failed`
- `ExecutionState = NotStarted | Running | Failed | TimedOut | Succeeded`
- `ValidationState = NotRequired | Pending | Passed | Failed`
- `attempts: Int64`
- `maxAttempts: Int64`

出力:

- `start`
- `wait`
- `retry`
- `validate`
- `complete`
- `needs-human`

保守後の policy では、execution が成功済みでも validation が Failed の場合、retry budget が残っていれば `retry`、残っていなければ `needs-human` です。さらに `TimedOut` は `Failed` と同じ retry policy を使い、budget があれば `retry`、なければ `needs-human` です。

## 実行

リポジトリルートから:

```sh
dotnet run --project mini-work-planner/runner -- Ready Failed Pending 1 3
```

出力:

```text
retry
```

例:

```sh
dotnet run --project mini-work-planner/runner -- Waiting NotStarted NotRequired 0 0
# wait

dotnet run --project mini-work-planner/runner -- Ready Succeeded Failed 0 1
# retry

dotnet run --project mini-work-planner/runner -- Ready Succeeded Failed 1 1
# needs-human

dotnet run --project mini-work-planner/runner -- Ready TimedOut Pending 0 1
# retry

dotnet run --project mini-work-planner/runner -- Ready TimedOut Pending 1 1
# needs-human
```

入力名は大文字・小文字を区別しません。

## 小さな policy check

```sh
dotnet run --project mini-work-planner/runner -- --test
```

各 state branch と retry boundary を覆う小さな matrix だけを確認します。

## 成功 snapshot からの materialize

#179 の成功 run など、maintained AuthoringSession が生成した `workspace.json` から:

```sh
python3 mini-work-planner/materialize.py /path/to/workspace.json
```

この処理は snapshot の JSON を semantic authority として直接読みません。

1. snapshot bytes を `planner.snapshot.json` にコピー
2. maintained AuthoringSession で open
3. 公開 `declarations` / `executionReadiness` / `emitCSharp` を取得
4. symbol manifest から runner を生成
5. 小さな policy matrix を実行

生成物は `declarations.json` と `runner/` 以下です。

## 出自

初期 program と maintenance change は #179 の bounded authoring probe で作成されました。最終成功 run は `20260918-212822` で、5型・3関数・whole-body maintenance 1回を commit、restart 後の identity continuity / execution readiness / C# symbol manifest を確認し、maintained request の reject は 0 件でした。
