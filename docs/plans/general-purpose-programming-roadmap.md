# 汎用プログラミングと言語・ツール機能の実装計画

- **Status:** Current
- **Updated:** 2026-09-26
- **対象:** 汎用的な言語機能、外部連携の契約、semantic graphの作成・保守ツール

## 目的と境界

Nyrwvyは、永続的な型付きsemantic graphをプログラム本体とし、ソーステキストを直接編集せずにプログラムを構築・変更する層である。生成コードは投影であり、意味論の決定権を持たない。

rrawrletは不足を発見する題材であり、製品固有のLeader、Member、attempt、Applyなどをコアへ追加する計画ではない。外部ライブラリを利用してよいが、アプリの判断・制御全体をhost関数へ移すことで言語上の不足を隠さない。非同期処理や実行制御も汎用機能によって表現する。

この文書は現在の到達点と次の作業を扱う。計画開始時点の制限、当時のPR状態、段階ごとの実装記録と調査根拠は[履歴](../../docs-archive/2026-09-general-purpose-programming/roadmap.md)へ分離した。履歴にある「未実装」は現在の残件ではない。

## 現在の対応状況

「完了」は各項目の限定された完了条件を満たすという意味であり、汎用言語としての全機能を備えたという意味ではない。正確な契約は[language subset](../reference/language-subset.md)とリンク先の仕様を参照する。

| ID | 到達点 | 現在の対応 | 残る境界 |
| --- | --- | --- | --- |
| G1 | 集計・実行時キー検索を通常authoringから実行 | `ListFold`、文脈付きcallback、先頭一致検索・要素取得の合成、文字列等値・結合・Int64変換、Pythonと同じ床除算・剰余の`Int64FloorDivide`/`Int64FloorModulo`（除数0でも失敗しない）、code point単位の`StringLength`と範囲を丸める`StringSlice`、`BoolNot`と右辺を必要時のみ評価する`BoolAnd`/`BoolOr`。`ListEffectFold`は順序付き効果と最初のErrでの停止に対応。 | 純粋なfold自体の早期停止、Map/Set（[保留の判断](../decisions/bool-operators-and-map.md)）や追加文字列操作は具体的要求に応じて選ぶ。 |
| G2 | 本体を複製しない文脈・型・述語の再利用 | 関数型パラメーター、明示的な文脈引数、[純粋・captureなしの関数値](../specs/pure-function-values.md)。 | 一般closure、型パラメーター付きProduct/Union、制約付き型パラメーター、効果付き関数値。 |
| G3 | 別モジュールの外部実装と型付き値・失敗の接続 | 明示的なC# `ExternalImplementation`、共通runnerの`hostSources`/`hostCapability`、複合値・`Result`・host例外の診断。[純粋なプログラム内実装](../specs/program-capability-implementations.md)。 | 状態・効果を持つプログラム内Capability実装。`FailureType`はC# backendで明示的に非対応。 |
| G4 | 構造化された並行処理と後始末 | `UseResource`、`ParallelAll`、協調的キャンセル、全workerの終了待ち、[並行Capability共有契約](../specs/parallel-capability-sharing.md)、[task scope](../decisions/concurrency-control.md)内のtask開始・待機（Any/All）・個別キャンセル（第1段階）と、`runPure`の仮想時計による決定的な待機時間制限（第2段階）。 | 宣言された並行安全性（Q-010）は必要が示された場合のみ再検討。独自scheduler、所有権・借用は未導入。 |
| T1 | reviewを伴う宣言変更・削除 | Product field追加・削除・型移行、引数/戻り値型移行、field付きvariant追加、不要関数・型・Effect・Capability/Operationの削除。 | Capability signature移行、Union variant field型移行、自動書き換え、汎用migration DSL。 |
| T2 | 複数・多段依存の再現と明示的更新 | 内容保持sidecar、同一内容の同一IDのみ共有、ダイヤモンド依存、更新previewとreview済みfingerprintに束縛した適用。 | 更新と局所migrationの同時公開、registry、オンライン配布、semantic merge。 |
| T3 | 共通の実行・テスト・診断経路 | `runPure`による型付き入出力・期待値・fake/host Capability実行、semantic ID付き診断、通常ツール/MCP。 | 新しい意味論に必要な入出力・診断を個別に追加する。完全なdebuggerは前提にしない。 |

G1-G4/T1-T3の開始時の課題を再実装する段階は終えている。[本文由来のEffect要求](../specs/body-effect-requirements.md)もinspect/reviewで利用できる。派生情報は宣言を自動変更しない。

## 次の作業順

2026-09-26の選択順は次の通り。

| 順序 | 作業 | 最小の範囲と完了条件 |
| --- | --- | --- |
| 1 | 現状と履歴の整理 | 本文を現在の対応表と作業順へ整理し、古い制限・調査・実装記録をarchiveへ保持する。この整理で完了。 |
| 2 | Nyrwvy内でCapability実装を選択（実装済み） | 各operationを既存の純粋・captureなしの関数へ対応付け、Capability値を作って渡す。同じ利用側関数へ2実装を渡して結果を切り替え、通常authoring・review・保存/再オープン・共通runnerを通す。operation不足・型不一致・不純な実装を公開前に拒否する。[実行・検証記録](../verification/program-capability-implementations-20260926.md)。 |
| 3 | abortの意味論を仕様化・実装（完了） | [型付き関数abort](../specs/typed-function-abort.md)は`Result<T,E>`関数のローカルな早期終了を定義する。解放・失敗優先順位・並行workerのキャンセルと終了待ちを明記した。[実装・検証も完了](../verification/typed-function-abort-20260926.md)。通常authoring、保存・再オープン、C#実行、解放と並行workerの失敗契約を確認した。 |

Capability実装は呼び出し側の既存Effect契約を自動で狭めない。実装が新たな効果を導入する一般形、状態・capture、継続を再開するhandlerはこの最初の段階に含めない。[Semantic independence](../concepts/semantic-independence.md)全体を採用したという意味でもない。

rrawrlet sliceの調査結果に基づく実装キューは[post-M2 roadmap](post-m2-roadmap.md#immediate-execution-queue)で管理する。

当該3作業とabort実装は完了。[通常ツールだけで構築する実用例](../../examples/function-abort/README.md)も追加した。次の機能拡張はこの経路で実際の処理を構築し、不足が確認されたものから選ぶ。

## 各作業で維持する条件

- 対象処理、現在の制限、最小変更、意味論・identity・reviewの不変条件、完了条件を明示する。
- 既存機能やライブラリで表現できるかを確認し、コアノードや共通基盤を必要以上に増やさない。
- 作成・inspect・変更の経路を共有ツール層/MCPから利用可能にする。機能ごとのtop-levelツール追加は必須ではない。
- 新規IDはシステムが割り当て、既存IDを保持する。未修正参照・型不正を公開しない。
- 作業開始時に対象ブランチと関連PRを確認し、完了済み・準備済みの仕事を重複させない。

## 検証と完了判断

実装した範囲で「authoring → 型・効果の検証 → review/commit → 保存・再オープン → lowering/backend → 実行結果・診断」を通す。保存形式やprotocolの変更には互換性・schema versionの扱いを明記する。

[Verification philosophy](../developer/verification.md)に従い、fastには重要な契約とregression、extendedには代表的な生成・実行確認を置く。既存のlive authoring成功記録は再利用し、同じモデル評価を反復しない。実環境でしか確認できない境界を変更した場合だけ、その検証を追加する。

## 条件付き・対象外の仕事

所有権、runtime state identity、独自scheduler、per-declaration hash、ストレージ変更、semantic mergeは具体的な要求や測定を起点に再検討する。ビルド時間の構造変更も[post-M2の条件](post-m2-roadmap.md#selection-rule-from-here)に従い、機能追加の前提にしない。

Codex/OpenCode/Git等の具体的アダプター、rrawrletのUI・運用ポリシー・実行データ永続化はアプリまたはライブラリの仕事である。

## 参照

- [Vision](../concepts/vision.md) / [Architecture](../developer/architecture.md)
- [Expression authoring](../specs/expression-authoring.md) / [AuthoringSessionと依存管理](../specs/agent-authoring-session.md)
- [C# backend](../specs/csharp-backend.md) / [Failure policy](../specs/failure-obligation-policy.md)
- [Open questions](../decisions/open-questions.md) / [計画と実装の履歴](../../docs-archive/2026-09-general-purpose-programming/roadmap.md)
