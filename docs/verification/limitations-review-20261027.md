# Nyrwvyの欠点 — 確認用資料

- **Status:** Observed result
- **確認日:** 2026-10-27（Asia/Tokyo）
- **対象:** `7f14552d75cb42ff9f1991a2e1c88e155a62a762` の実装・仕様・既存検証記録。作業ツリーの未commitの実装変更は評価対象に含めない。
- **目的:** 利用や開発を進めるうえでの弱点を確認するための資料。新しい仕様・採用済みの改善計画ではない。

## 結論

**Nyrwvyの主な欠点は、意味を検査して変更する仕組みを使うために、編集・確認・アプリとの接続に手間がかかること。** 小さなプログラムを作成・変更・実行する経路は成立しているが、大規模な編集、複数人での開発、一般的なアプリへの組み込みには制約が残る。

とくに確認したいのは、①レビューを完了するまでの操作量、②アプリごとに必要なホスト接続、③小さな変更でも発生する大規模グラフの処理費用。これは本資料の評価であり、既存ロードマップの優先順位を変更するものではない。

### 全体像

| 欠点 | 利用時の影響 | 根拠の種類 |
| --- | --- | --- |
| 1. 構造編集の入力・確認が重い | recipeの形やスコープを調べ、大きなレビューを分割取得する必要がある | 現行契約＋移植時の観測 |
| 2. ホスト連携に個別の接続処理が要る | UI・外部サービスとつなぐ費用が残る | 1アプリでの観測 |
| 3. 小さな変更にも大きな処理費用がある | グラフが大きくなると編集の待ち時間・割当量が増える | 既存の計測 |
| 4. 共同編集・意味単位のマージが未整備 | 同じ基底からの変更が古くなると、再提案・再確認が必要 | 現行契約 |
| 5. 停電耐久性と複数ファイルの一括保存に限界がある | 障害後の復旧・バックアップを運用側で考える必要がある | 現行契約 |
| 6. 言語内での抽象化に制約がある | クロージャや状態付きの実装を別の形で表現する必要がある | 現行契約 |
| 7. 人間向けの全体把握が弱い | 関数をまたぐ理解やレビューをツールの問い合わせで補う | 実装範囲＋評価 |
| 8. 実行先の選択とコードの隔離に制約がある | backendごとの対応範囲、ツールの.NET依存、実行権限を考慮する必要がある | 現行契約＋実装 |
| 9. 効果の一般化・第三者による追試に限界がある | 「従来の開発より有利」と広く判断する根拠は不足 | 検証記録の範囲 |

**読み分け:** 「現行契約」は現在の仕様・実装で確認した範囲。「観測」「計測」は記録された条件での結果。「評価」「推論」はそこからの判断であり、実験で確定した事実ではない。

## 1. 構造編集の入力・確認が重い

**事実:** 通常の編集では、型付きのrecipeを作り、simulateで得たレビューを確認し、明示的にcommitする。recipeには厳密なプロパティ名やスコープ参照がある。prompt-desk移植では、`listLength.value`、`mapKeys.value`、`float64Literal.bits` の形を参照し直して修正した。

現在は `nyrwvy_recipe_reference` があり、大きなレビューにも省略数・`requiresDetailedReview`・`readProposal` による続きの取得がある。ただしcompact応答は16 KiB、詳細のページは最大8 KiBで、必要な範囲を取得して読む手間は残る。

移植時には、独立したeffect付きの式を `Bind` で順序付け、effect付きcallbackを `ListMap` から `ListEffectFold` に書き換えた。暗黙の評価順に頼れないため、従来の処理を移す際には順序とeffectの表現も見直す必要がある。

**影響・評価:** 計算内容だけでなく、編集APIの表現とレビューの取得方法にも習熟が必要。大きな変更では、変更を書く費用に加え、何を読み終えたかを管理する費用がかかる。安全な変更確認の対価であり、検査を外せばよいという意味ではない。

**不確かな点:** 移植時に記録された「応答エラーでもproposalが残った」事象は、現行版での再現試験を行っていない。現行の不具合とは断定しない。現行ツールでの操作時間と、移植時との差も未計測。

根拠: [移植時の観測](../plans/prompt-desk-port-improvements.md)、[現在のツール説明](../../tools/nyrwvy-agent/graph-tools.mjs)、[式の構造編集](../specs/expression-authoring.md)。

## 2. ホスト連携に個別の接続処理が要る

**観測:** 2026-10-24のprompt-desk移植では `画面 → HTTP → Pythonのプロセス管理 → 生成C#` を接続し、ECMAScript互換操作にNode.jsを使った。型変換、UTF-16の保持、プロセス再利用、業務エラーと通信障害の分類を実装した。30万件のタグ索引は毎回転送せず、Runner内で保持する参照方式にした。

**現在ある支援:** `libraries/host` にファイル・環境変数・プロセス・PTYの共通Capability実装があり、`emitCSharp`は読みやすい名前のfacadeとCapabilityの基底クラスを生成できる。既に共通化された部分まで、アプリごとに一から実装する必要はない。一方、ライブラリの利用にはC#の生成・ホスト実装の組み込み・Capabilityの受け渡しが必要で、これだけでGUIや外部ライブラリの接続全体が自動生成されるわけではない。

**影響・評価:** 業務ロジックをNyrwvyで書けても、アプリとして使うまでの配線は別の負担になる。複数言語・プロセスの境界が増えると、障害の切り分けやライフサイクル管理も複雑になる。

**範囲:** 移植費用の具体例は1アプリの過去の観測。現在のホストライブラリやfacadeを使った場合に、同じ費用がどこまで減るかは再測定していない。残る接続処理の共通化範囲・他アプリでの費用・必要工数も未調査。

根拠: [移植時の観測と提案](../plans/prompt-desk-port-improvements.md)、[現在のホストライブラリ](../../libraries/host/README.md)、[emitCSharpのfacade生成](../specs/agent-authoring-session.md#operations)。

## 3. 小さな変更にも大きな処理費用がある

**計測:** 2026-10-25の最適化後も、27.36 MiBの`.nyrec`（12,000関数・105,000ノード）に対する関数名の変更で次の費用が記録されている。

| 操作 | 時間 | managed割当量 |
| --- | ---: | ---: |
| open | 1,783.7 ms | 1,429.1 MiB |
| simulate-rename | 2,356.8 ms | 2,558.6 MiB |
| commit-rename | 1,313.3 ms | 1,728.5 MiB |

同条件のcommitは最適化前から時間55.0%、割当量48.4%改善している。それでも、名前変更のような小さい変更に秒単位の処理が残った。

**影響・推論:** 編集を小分けに繰り返す使い方では待ち時間が積み上がり、割当の多さはGC負荷につながりうる。保存形式を行単位にしただけでは、検証・fingerprint・直列化を含む処理全体の費用はなくならない。

**数値の限界:** Workstation GC、warmup除外後3回の中央値、通常のAuthoringSession APIでの計測。固定サイズの関数を複製したfixtureで、obligationや依存sidecarはない。MCP通信・起動時間を含まない。割当量は呼び出しスレッドの累積managed bytesで、最大RAM使用量ではない。現行HEADでの再計測も行っていない。

根拠: [最適化後の比較・条件・再現情報](nyrec-optimization-20261025.md)。

## 4. 共同編集・意味単位のマージが未整備

**事実:** 1つのworkspaceを1つのauthoring sessionがロックする。proposalは正確な基底に結び付いており、古いproposalは拒否される。外部変更を検出しても自動merge・rebaseはしない。一般的なsemantic mergeとGit上の意味単位の競合解決は未解決事項として残る。

**影響・評価:** 同じworkspaceの並行更新には向かない。別々に進めた変更でも、基底が変われば再検査・再提案・再レビューが必要になる。stable IDは対象の同一性を保つが、変更の競合を自動解決するものではない。

**設計上の理由:** 未確認の再適用を防ぐ仕組みでもある。欠点は「staleを拒否すること」自体より、その後の共同作業を支える手順・ツールがまだ限定的なこと。

根拠: [single-writerと外部変更](../specs/agent-authoring-session.md#single-writer-and-external-modification-rule)、[Q-005・Q-014](../decisions/open-questions.md)。

## 5. 停電耐久性と複数ファイルの一括保存に限界がある

**事実:** snapshotの置換はatomicだが、OS crash・停電後の耐久性は保証されない。ファイルと親ディレクトリを永続媒体へ明示的に同期する契約はない。依存関係を持つworkspaceでは、snapshotと`.dependency.json`の2ファイルを跨ぐcrash atomicityも保証されない。

通常の書き込み失敗にはrollbackがあり、復旧用の`.previous`もある。ただし2回の置換の間でprocessが落ちる場合まで、一括保存の成功を保証するものではない。未commitのproposalもprocess-localで、終了後は再simulate・再reviewが必要。

**影響・評価:** commit成功と停電後の保存保証を同一視できない。依存sidecarを含めたバックアップと、失敗・応答喪失時に保存状態を確認する復旧手順が必要になる。

根拠: [保存保証と障害境界](../specs/agent-authoring-session.md#persistence-guarantee-and-failure-boundaries)、[依存snapshotの保存](../specs/agent-authoring-session.md#pinned-snapshot-dependencies)。

## 6. 言語内での抽象化に制約がある

**事実:** 関数値はpure・capture-freeの名前付き関数を中心とし、genericな関数を値として参照するには具体的wrapperが要る。capturing closureとeffectful function valueはこの実装範囲外。プログラム内で作るCapability実装も、monomorphicで推移的にpureな関数に限られ、状態やcaptureを持たない。

**影響・評価:** 周囲の値を閉じ込めるcallbackや、状態を持つサービス実装を、そのままの形では表せない。明示的な引数・context・状態の受け渡し、または外部host実装が必要となり、一般的な言語の短いコードより構造が増える場合がある。

**注意:** `Bind`、関数値、program-defined Capability、`ListEffectFold`は既にある。「高階関数やeffectを扱えない」という欠点ではない。また、状態付き処理が一切書けないという意味でもない。

根拠: [pure function values](../specs/pure-function-values.md)、[program-defined Capability](../specs/program-capability-implementations.md)、[現在の言語範囲](../reference/language-subset.md)。

## 7. 人間向けの全体把握が弱い

**事実:** Function FocusとChange Reviewは実装済みで、`nyrwvy_view`からHTMLを生成できる。一方、複数関数・subsystemを跨ぐworkspace全体のviewとlayoutはQ-008で未解決。

**影響・評価:** 関数単位の理解と、プログラム全体の責務・依存・影響範囲の把握には距離がある。queryや個別viewを行き来する負担は残る。正本が通常のsource textではないため、慣れたエディタやテキストdiffによる読み方を、そのまま正規の編集経路には使えない。

**不確かな点:** 今回は画面を使ったユーザビリティテストをしていない。既存viewが読みにくい、グラフ表示が常にコードより劣る、とまでは結論しない。

根拠: [Q-008](../decisions/open-questions.md)、[Core modelとAI graph authoring](../../README.md)。

## 8. 実行先の選択とコードの隔離に制約がある

**事実:** C#／.NETに加え、`runPure`は `backend: "zig"` を選べる。Zigは未対応の構造を `RunBackendUnsupported` で拒否し、C#の `hostSources` には対応しない。AuthoringSession自体はF#／.NET製なので、Zigで生成プログラムを動かしても、編集ツールの.NET依存はなくならない。通常のMCP導入にはNode環境も必要。

2026-10-24のprompt-desk移植では、ブラウザー内で直接動く経路を使えず、ローカルサーバーを接続した。現行の `runPure` も、ローカルでコンパイルして子プロセスを実行する経路である。

`runPure`は時間・出力・値サイズを制限するが、resource sandboxではなく、memory上限もない。`hostSources`は既定で無効だが、有効にすると任意のC#をhost起動ユーザーの権限で実行する。snapshotのファイルアクセスもserver processの権限範囲に依存する。

**影響・評価:** ブラウザーだけで完結する配布や、信頼できないプログラムを隔離して実行するサービスには追加の基盤が必要。型・effectの検査や変更レビューを、OSの権限分離の代わりにはできない。

**範囲:** 新しい脆弱性の発見ではなく、明示された実行上の境界。Zigの全機能でのC#との同等性や、各OS・配布先での動作を今回確認したわけではない。READMEとSECURITYにはC#のみを説明する記述が残るため、backendの有無は現在のprotocolと実装を優先した。

根拠: [backendの選択と制約](../specs/agent-authoring-session.md#operations)、[実行実装](../../tools/Nyrwvy.AuthoringSession/PureRun.fs)、[AuthoringSessionの.NETプロジェクト](../../tools/Nyrwvy.AuthoringSession/Nyrwvy.AuthoringSession.fsproj)、[実行と権限の境界](../../SECURITY.md)、[MCP導入](../../tools/nyrwvy-mcp/README.md)。

## 9. 効果の一般化・第三者による追試に限界がある

**事実:** M1・M2には条件を固定したagent評価がある。一方、現在の広い言語機能すべてに独立した大規模モデル評価があるわけではない。移植probeの多くは同じ作者の別アプリを対象とし、probe scripts・oracle・raw casesは非公開で、第三者が比較実験をそのまま再実行できない。

READMEは1人のmaintainerによる研究・実装プロジェクトと明示し、外部PRは現段階で受け付けていない。CIもpush／PRごとの自動実行ではなく、節目で手動起動する方針。

**影響・評価:** 「この条件で変更・実行できた」という根拠はあるが、異なるアプリ・agent・チームでも従来の開発より速く安い、という一般的な主張はまだ支えられない。導入側が必要なワークフローを自分で検証する負担が残り、保守も1人に依存する。

**注意:** 非公開の比較probeと、公開されている実装・決定的テスト・実行例は区別する。テストがない、検証結果がすべて信用できない、という意味ではない。

根拠: [probeの再現可能性](README.md#live-agent-probes)、[言語範囲のEvidence status](../reference/language-subset.md#evidence-status)、[Status and evidence・Build and verify](../../README.md)。

## 今回、欠点として数えなかったもの

- **「record形式はまだ保存に使えない」:** 古い記述。現行Sessionは`.nyrec`をRecordCodecで読み書きする。
- **「backendはC#だけ」「共通のホスト実装がない」:** 現行にはZig backend、ホストライブラリ、C# facade生成がある。残る制約は対応範囲と個別接続の負担。
- **「大きなレビューに続きの取得方法がない」:** 現行は省略情報と`readProposal`を備える。残るのは取得・確認の操作量で、過去の応答失敗は未再現。
- **「Map・Bytes・tail self-callなどがない」:** 過去のprobeの不足を、そのまま現在に持ち込まない。現在の言語範囲を優先する。
- **「構造が正しいから、動作も正しいはず」:** その保証はないが、Nyrwvy自身もテストケースでの一致を一般的な同値性の証明とはしていない。業務仕様の検証は引き続き必要。
- **元言語との完全互換や非同期アプリ一般の競合:** 移植費用ではあるが、すべてをNyrwvy固有の欠点には数えない。

## 確認したい判断

1. **直近の目的は単独のAI編集か、チーム開発か。** 後者なら共同編集とsemantic mergeの制約が先に効く。
2. **実アプリを増やす予定があるか。** あるならホスト接続を繰り返し書く費用を確認したい。
3. **通常扱うsnapshotはどの程度の大きさか。** 大きいなら名前変更だけでなく、普段の式編集で待ち時間とRAMを測りたい。
4. **失ってはいけない保存状態と、実行するコードの信頼範囲は何か。** 必要な保存保証・隔離と現在の契約の差を先に決めたい。

以上は次の調査を選ぶための論点であり、機能追加の一括提案ではない。

### 調査の限界

今回行ったのは、現行仕様・一部実装・既存記録の照合。新しい性能計測、障害注入、agent比較実験、Nyrwvy本体のUI操作、脆弱性診断は行っていない。とくに過去の移植結果と計測値は、その記録の条件を越えて一般化しない。
