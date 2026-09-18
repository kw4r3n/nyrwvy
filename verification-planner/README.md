# Verification Planner

maintained AuthoringSession で作成した小さな純粋プログラム。semantic authority は `planner.snapshot.json` を開いた session であり、JSON の直接編集・直接読解は行っていません。`runner/Generated.cs` は公開 `emitCSharp` の未編集出力です。

## 実行

リポジトリルートから（.NET 10 SDK が必要）:

```sh
dotnet run --project verification-planner/runner -- Documentation
dotnet run --project verification-planner/runner -- Semantics,Documentation --regression
dotnet run --project verification-planner/runner -- AuthoringProtocol --agent
dotnet run --project verification-planner/runner -- EvaluationHarness --environment
```

出力は順に `fast tests のみ`、`fast + extended`、`fast + live`、`fast + extended + live`。

領域名: `Documentation`, `Semantics`, `AuthoringProtocol`, `Backend`, `Persistence`, `EvaluationHarness`, `CI`。大文字・小文字を区別せず、カンマで複数指定できます。`--agent`, `--environment`, `--regression` はそれぞれ Bool 入力です。CLI は入出力アダプターにすぎず、判定は生成された NyamNyam 関数が行います。

```sh
dotnet run --project verification-planner/runner -- --test --maintained
```

## 型・関数

- `ChangeArea`: 上記7ケースの Union。
- `ChangeRequest`: Product。`areas: List<ChangeArea>`, `agentInteraction: Bool`, `environmentDependent: Bool`, `knownRegression: Bool`。
- `VerificationLevel`: `FastOnly | FastExtended | FastLive | FastExtendedLive`。
- `area_requires_extended: ChangeArea -> Bool`: 初期は Semantics / Backend / Persistence。保守変更で EvaluationHarness を追加。
- `requires_live: ChangeRequest -> Bool`: agentInteraction または environmentDependent。
- `plan_verification: ChangeRequest -> VerificationLevel`: ListAny で extended を集約し、live と組み合わせる。
- `verification_label: VerificationLevel -> String`: 4種類の表示文字列。

fast は常に必要。knownRegression は情報として保持し、検証レベルを増やしません。Documentation / AuthoringProtocol / CI だけでは extended を増やしません。ただし live の明示フラグは領域に関係なく適用します。「documentation のみ」は live フラグも false の通常の文書変更を意味します。空リストは領域由来の extended なし、重複や順序は ListAny の判定に影響しません。

## 作業・検証記録

1. 3型と4関数を simulate → review 確認 → commit。初期実装完成。
2. 公開 emitCSharp の出力をコンパイルし、7領域の全128部分集合 × 3フラグの全8組合せ = **1,024ケース合格**。
3. session を一度終了・再起動して declarations を再発見。全 TypeId / VariantId / FieldId / FunctionId / ParameterId と署名の維持を確認。executionReadiness は `ready`、関数数4。
4. focusFunction で EvaluationHarness の分岐と caller を確認。simulateReplaceTarget でその BoolLiteral false だけを true に変更。review の base/proposed focus と影響を確認して **1回だけ保守 commit**。
5. 保守変更後も1,024ケース合格、executionReadiness は ready。全宣言・署名の discovery 応答が再起動直後と一致。commit 後の focus が review の proposedFocus と完全一致。

影響先は plan_verification 内の ListAny 1箇所。署名変更も caller 書き換えも不要なので parameter-type migration は使用していません。対象グラフノードを含め IDs の追加・削除はありません。

主要な証跡:

- `transcript.jsonl`: 全公開 JSONL request / response（review、commit 含む）。
- `initial-declarations.json`, `restart-declarations.json`, `maintained-declarations.json`: 公開 discovery 応答。
- `restart-check.json`, `maintenance-check.json`: 比較結果・操作数。
- `maintenance-focus.json`, `maintenance-review.json`: 対象・caller・変更前後の review。
- `initial-execution.log`, `maintained-execution.log`: 実行テスト結果。
- `Initial.generated.cs`: 初期実装の未編集 backend projection。

## 摩擦・失敗・制約

- semantic request の reject は **0件**、proposal の再試行も **0件**。createType 3回、createFunction 4回、replaceTarget 1回、commit 合計8回。
- 再起動時に fields / variants の列挙順が ID 順に変わったため、単純な discovery JSON 完全一致チェックが1回失敗。ID で順序を正規化し再確認したところ、変更は列挙順のみ。関数・parameter 配列を並べ替える回避策は使っていません。
- `--help` は usage を返し exit 2。ブラウザの Node 動的 import で subprocess を確保する試みは1回失敗。いずれも semantic request の拒否ではありません。
- 宣言 discovery を繰り返す必要があり、この作業では13回。review は nested graph の文字列表現と flattened node 表現が重複し、単一 Bool の変更に対して大きい payload でした。
- targeted diff の previousTarget には Bool 値そのものが出ないため、baseFocus / proposedFocus の値も確認しました。nested match の branch と対象 ID の対応も文字列表現から確認しました。
- JSONL のキーが仕様書だけでは十分に分からず、transport parser のみ参照しました。Core 実装・raw snapshot をプログラムの semantic authority にしていません。
- Bool OR 専用 recipe の代わりに conditional を使用。複数領域は List + ListAny で自然に表現でき、要求を損なう blocker はありません。
- JSONL の直接実行操作ではなく、公開 emitCSharp → .NET コンパイルという実行境界を使用。CLI アダプターは生成 symbol の ID エンコーディングに依存し、その symbol が実際の出力に存在することを verify.py で検査しています。
- repository 自体の実装は変更していないため、repository 全体の fast / extended テストは実行していません。

## 再発見・再生成

```sh
python verification-planner/session_client.py start open
python verification-planner/verify.py maintained
python verification-planner/session_client.py close
```

session_client.py は JSONL を中継するだけの作業用 relay（最大30分で終了）。snapshot を解釈しません。`author.py` は初期 authoring intent の記録・補助であり、自動 commit ワークフローではありません。新しい proposal は必ず review を確認してから commit してください。
