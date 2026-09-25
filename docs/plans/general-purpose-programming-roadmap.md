# 汎用プログラミングと言語・ツール機能の実装計画

- **Status:** Historical（完了した計画の記録。G1–G4、T1–T3の完了条件を満たした。T3に残る既知の非対応は§3のT3に記録）
- **Prepared:** 2026-09-24
- **Updated:** 2026-09-25（PR #214・#215のマージ後。次の実装キューは[post-M2 roadmap](post-m2-roadmap.md#immediate-execution-queue)）
- **対象:** Nyrwvyの汎用的な言語機能、実行・外部連携の契約、semantic graphの作成・保守ツール
- **基準:** main `23a2df028dfbdda046382c26c00ee6d2a7794368`。計画策定時点で未マージだったPR #214は別枠で確認した（2026-09-25にマージ済み）。
- **位置付け:** 当面の実装目標。既存仕様を上書きせず、個々の機能の具体的な意味論は実装着手時に決定・仕様化する。

## 1. 目的と境界

**Nyrwvyは、プログラム言語の代替として、ソーステキストを直接編集せずにプログラムを構築・変更するための層である。** 永続的な型付きsemantic graphがプログラム本体であり、生成コードはその投影である。[Vision](../concepts/vision.md)のこの方向を、実用的なプログラムの表現力と保守可能性まで広げる。

rrawrletは不足を発見するための題材であり、Nyrwvyに組み込む製品機能の一覧ではない。問うのは「rrawrlet相当のプログラムをNyrwvyで記述・変更するために、言語とツールに何が足りないか」である。本計画はrrawrletの移植作業そのものや、そのエージェントにNyrwvyを利用させる計画ではない。

| 層 | 本計画の対象 | 本体に追加することを目標にしないもの |
| --- | --- | --- |
| 言語・意味論 | 値、型、関数、制御、効果、失敗、非同期処理等を表現する汎用的な能力 | Leader、Member、attempt、Applyなどのアプリ固有概念 |
| 外部連携・ライブラリ基盤 | 型付きの外部宣言、binding、値の受け渡し、依存解決、backendでの実行 | Codex/OpenCode接続、Git、HTTP、JSON、ファイル操作等の具体的な実装を本体へ個別追加すること |
| authoring・開発ツール | graphの作成・変更・削除、影響確認、依存管理、実行・検証・診断 | rrawrletのUI、運用サービス、アプリ固有テストハーネス |

汎用ライブラリやSDKは利用してよい。Nyrwvy側の不足を調べる際には、ライブラリが存在しないことと、ライブラリを定義・参照・呼び出す機能がないことを分ける。あらゆる便利な操作を新しいコアノードにする必要はない。一方、外部関数へアプリの処理全体を移して呼び出すだけでは、言語の不足を解消したことにならない。

「判断だけNyrwvy、実行基盤は既存言語」という分割は、個別の導入方法としては可能だが、本計画の到達目標や固定アーキテクチャにはしない。非同期処理や実行制御も、必要な汎用機能を通じて記述可能にすることが検討対象になる。

## 2. 現状と評価方法

既存機能には、Product/Union、Option/Result、条件分岐と網羅的match、直接関数呼び出し、Effect/Capability、`Bind`による順序付けと値の再利用、型・関数等の作成、部分式置換、review/commit、保存・再オープンがある。関数の引数追加、明示的なrequired-effect変更、限定的な引数型移行、fieldless Union variant追加も存在する。「副作用がない」「値を再利用できない」「宣言を変更する手段が一切ない」とは扱わない。[S1][S2][S3]

不足は、次の3種類で記録する。

- **確認済みの制限:** 現在の型・ノード・公開操作・backendで対応範囲が限定されている。
- **方式を決める必要がある目標:** 必要な能力は明確だが、新しいプリミティブ、既存構成の拡張、ライブラリのどれで満たすかは未決。
- **実装済み・レビュー待ち:** 開いているPRに実装があり、新規開発として重複させない。

### 調査時点の既存作業

PR [#214](https://github.com/kw4r3n/nyrwvy/pull/214)、head `d94e536c60253ddee36ac6d11e2b5e81eb72dd26`は調査時点でopen（2026-09-25にマージ済み）。MCP/OpenCodeからのdependency指定、依存先の正確な内容をsidecarに保持する機能、既存pinの明示的な復旧・移行経路が実装されている。[S4]

これらはmainの不足ではあるが、新規実装目標としては重複させない。内容保持は同じ版の再現・移動を改善するもので、変更された依存先へのsemantic upgrade、複数・多段依存まで解決したという意味ではない。各作業の開始時にmainと関連PRの状態を再確認する。

本計画の調査は仕様・ソース・PR差分の照合であり、新しいビルド、実行プローブ、全体移植を行った証拠ではない。

### G2 文脈引数による関数の再利用

G2の最初の方式として、closureや型パラメーターではなく明示的な文脈引数を採用した。`ListFold`/`ListMap`/`ListFilter`/`ListAny`/`ListAll`は任意の`context`を持ち、callbackの一部の引数をParameterIdで反復前に評価した値へ束縛する。残りの未束縛引数が宣言順に反復値（foldでは蓄積値と要素）を受け取る。これにより、同じ述語・変換・畳み込み関数を実行時の閾値や係数など異なる文脈に本体を複製せず適用できる。検証（束縛先の存在・重複・型、未束縛引数のarity）、JSON/Nyason/typed IR/Execution IRの保存（空のcontextは書かず既存グラフのバイト列とfingerprintを維持）、lowering、C#のlambda生成、ID指定・表示名指定のrecipeを追加し、通常のauthoringから共通runnerでfilter/map/fold/any/allを実行した。callbackは引き続き純粋に限る。効果付き反復は別の構成として`ListEffectFold`を追加した。folderは囲む関数が宣言した効果だけを使え、Capabilityを文脈引数として受け取り、要素ごとに1回ずつ左から順に実行する。folderが`Result<S, E>`を返す場合は最初の`Err`で停止してそれを返し、以降の効果を実行しない。fake Capabilityを使った共通runnerで、呼び出し順序・1回実行・`Err`での停止（4要素中3回で停止）を確認した。続いて関数の型パラメーターを追加した。`TypeRef.TypeParameter`を引数型に含む関数が汎用関数となり、型パラメーターのIDはシステムが割り当てる（作成要求では名前で指定）。呼び出しやcallbackでは、引数・要素・蓄積値・資源の型と引数型を構造的に照合して型引数を決める（一般的な型推論は行わない）。汎用関数の本体では型パラメーターは不透明で、宣言された型（Product/Union）やCapabilityには使えない。C#では汎用メソッドとして生成する。同じ`count`・`wrap`・`orElse`の本体を`List<String>`・`List<Int64>`・`Option<String>`などへ複製せずに適用し、汎用関数から汎用関数への呼び出し、汎用callbackの`ListMap`、型の衝突や未宣言の型パラメーターの拒否を通常のauthoringと共通runnerで確認した。これで文脈引数と合わせてG2の完了条件を満たす。関数値・closure、型パラメーター付きの宣言型、制約付き型パラメーターは未導入。

### G4 スコープ付きリソース（第1段階）

G4の最初の段階として、非同期より先にスコープ付きリソースの後始末を追加した。`UseResource`は`Result<R, E>`を返す取得式、R型の資源を受け取る本体関数（他の引数は文脈引数）、Capabilityの解放operationからなり、取得に成功した場合は本体のOk/Errのどちらの出口でも、また本体のhost例外の伝播前にも必ず解放を実行する。失敗の合成は「本体のErrを優先し、本体が成功した場合だけ解放のErrを返す」、host例外中の解放失敗は元の例外を隠さない、と明示した。値は引き続きmanaged（Q-012は未変更）で、所有権や一意性は保証しない。fake Capabilityで成功・取得失敗・本体Err・解放Err・両方のErr・host例外の6経路の呼び出し列と結果を確認した。続いて構造化された並行実行として`ParallelAll`と`CancellationRequested`を追加した。`ParallelAll`は要素ごとの作業関数（`Result<U, E>`を返し、効果と文脈引数のCapabilityを使える）を並行に開始し、必ず全ての作業の終了を待つ。最初の`Err`またはhost例外で他の作業にキャンセルを要求し（`CancellationRequested`と外部実装が見る共通のtokenによる協調的停止）、結果は時刻に依存しない最小indexの`Err`、全て成功ならsource順の`Ok`とした。fake Capabilityに「キャンセルされるまで待つ」応答を加え、2つの独立した処理の開始・待機・片方の失敗による他方の停止・両方の終了・`UseResource`による後始末（`close`が両方の実行の後）・host例外の伝播を実行で確認した（反復実行でも安定）。これでG4の完了条件を満たす。タスク値を持ち回る非構造な非同期、タイムアウト、スケジューラの選択は対象外で、Q-012（所有権）は変更していない。

### T2 複数依存と衝突方針

Q-002の取り込み方針を「型付きIDを保持し、複数依存で同一IDは内容が完全一致する場合だけ1回取り込み、それ以外の衝突は拒否（統合しない）」と決定した。`create`で`--dependency`を複数指定でき、各依存の正確な内容を埋め込んだ`{ "dependencies": [...] }`のsidecarを保存する。結合後の依存グラフは構成要素のfingerprintから順序に依存しない決定的なSnapshotIdを持つ。共通の型を含む2つのライブラリを同時に参照する関数を作成・保存・再オープンし、異なる内容の同一ID・同一依存の重複指定・埋め込み内容の改ざんを拒否することを確認した。続いて多段依存に対応した。自身の依存を持つワークスペースを依存先に選ぶと、その保持済みsidecarに対して局所宣言を解決した完全なグラフへ平坦化して固定する。中間ライブラリと上流を削除しても再オープンでき、同じ基底ライブラリへ複数経路で到達するダイヤモンド依存は同一内容として1回だけ取り込まれることを確認した。最後に明示的な更新を追加した。セッション内の`previewDependencyUpgrade`が新版を候補として固定し、追加・削除・変更された取り込み宣言と、変更しない局所グラフの新版に対する検証結果を返す（書き込みなし）。適用は`upgrade`起動モードで、レビュー済みのfingerprintと一致し、局所グラフが新版で妥当な場合だけsidecarと局所snapshotの依存束縛を書き換える。互換な更新の適用、互換でない更新・未レビュー版・同一版の拒否（ファイルは不変）、複数依存の一部だけの更新を確認した。これでT2の完了条件（複数・多段依存、独立ライブラリの再利用、意図した版の再現と明示的更新による影響確認）を満たす。局所参照を新版へ同時に移行する更新（更新とmigrationの合成）、registry、オンライン配布は対象外。

### T1のCapability operation削除

`DeleteCapabilityOperation`を追加した。Capability全体や兄弟operationを保持し、未参照のoperationとそのparameterだけを削除できる。既存のsnapshot検証を再利用して生存する呼び出しを拒否し、削除IDをreview／JSONL／HTMLに残す。関数削除→operation削除→不要Effect削除を同じtransactionに合成できる。空Capabilityを許容し、依存先のoperation編集は拒否する。Capability signature変更や参照元一括移行を完了したものではない。

### T2の既存作業の統合

PR #214（head `d94e536`）の内容をローカルmainへ統合した。依存snapshotの内容保持、通常ツールからの依存指定、同一版に限ったpin復旧・移行を再利用している。ソース所有ディレクトリの整理に合わせてG1の依存関係投影を移し、追加済みの実行テストに`Suite=Extended`を付けた。T1の削除操作は依存先の関数・型・Capability・Effectをsimulation時にも拒否する。runPureは元の依存ファイル削除後もembedded contentを利用し、manifestの外部変更は拒否する。複数・多段依存、新版へのsemantic upgrade、G2の型抽象化は引き続き未実装。GitHub上のPR状態とは別に、これはローカルでの統合記録である。

### 着手後の記録（G1 第1段階）

`feat/g1-data-operations` で純粋な `ListFold(source, seed, folder)` を追加した。蓄積型 `S` と要素型 `T` に対して既存の関数 `f : (S, T) -> S` を参照し、左から右へ処理する。seedに実行時キーを保持した検索と非可換の集計を、通常の関数authoring、review/commit、JSON/Nyason保存、lowering、C#実行まで小さく確認した。当初の検証は序数的 `StringContains` を双方向に合成して完全一致を表現していた。後続で意図を直接表す専用の `StringEqual` に置き換えた。一般的な要素取得も現段階では型ごとにfold関数を作成する。この記録はG1/T3全体の完了を意味しない。以下の「確認した制限」は計画策定時点の記述であり、foldの不足はこの第1段階で解消した。

`feat/g1-data-operations` の後続ではG3の最初の境界として、既存のCapability interfaceに外部実装を注入する明示的なC# profileを追加した。型付きProduct入力と`Result`による正常な失敗をfake hostで往復し、NyrwvyのMatchResultで分岐することを確認した。`FailureType`やhost例外をResultへ暗黙変換しない。新しい外部オブジェクト表現、実装の自動ロード、リソース寿命・T3の共通実行入口は未着手であり、G3全体の完了宣言ではない。

その後、共通runnerに明示的なhost実装の登録を追加した。`runPure`の`hostSources`に別モジュールのC#ファイルを指定し、Capability型の引数を`hostCapability`で実装型へ結び付ける。runnerは生成したdecoratorで呼び出しを記録し、host例外をCapabilityOperation/Functionのframe付き`RunRuntimeFailure`として返す。Product引数と`Result`による型付き失敗を持つlookupを、コアの操作一覧・生成コード・手書きrunnerを変更せずに実行した。これによりG3の完了条件（別モジュールの外部関数の明示的登録、複合値と型付き失敗、bindingの追加でコア変更不要）を満たす。`FailureType`はQ-010の方針に従い、具体的な利用者が現れるまでC# backendで明示的に非対応のままとする。外部オブジェクト（opaque handle）の専用表現は、現在のCapability値で表現できる範囲を超える要求が出た時点で検討する。

G1の後続検証では、`ListFold`の蓄積値に残りindexと`Option<String>`を持たせ、通常の関数authoringから型付き`item_at : List<String> * Int64 -> Option<String>`を構築・保存・実行した。空、範囲外、負のindexを確認し、専用のListAtノードを増やさず要素取得を表現できることを確認した。次の段階では序数的`StringEqual`も追加し、双方向`StringContains`による完全一致の合成を簡潔にした。双方向の包含は等値と同値であり、以前の記録にあった誤一致の説明は誤りだった（`"ab"`対`"abab"`は従来の合成でも不一致）。foldによる実行時キー検索を作成・保存・lowering・C#実行まで通した。後続で順序・空文字・Unicodeを保つ`StringConcat`も、JSONLでのauthoring、保存・再オープン、Nyason、lowering、C#実行を通した。またProductの蓄積値に実行時キー・発見フラグ・`Option<String>`の結果を持たせたfoldにより、別の値を持つ同一キーが複数あっても最初の値を返し、未発見・空入力で`None`となる文脈付き検索を通常のauthoring/保存/C#実行で確認した。専用のfindノードは追加していない。その後、数値との変換として`Int64ToString`（不変カルチャの正規10進表記）と`StringParseInt64`（`-`と ASCII数字だけを受け付け、範囲外や他の表記は例外ではなく`None`）を追加し、JSONL authoring、保存・Nyason、lowering、共通runnerでの実行まで確認した。型ごとの関数本体複製を避けるG2の型抽象化、List自体の早期停止、効果付き反復などは未解決。

T3の最初の共通実行入口としてauthoring sessionに`runPure`を追加した。保存済みプログラム中の関数を`FunctionId`、型付きString/Bool/Int64引数、任意の期待値で指定し、C#生成・一時ビルド・別プロセス実行を経てsnapshot/functionのidentityと実結果・一致可否を返す。生成ソースの手修正や関数ごとの手書きrunnerは要らない。続いてString/Bool/Int64のflat List入出力・期待値にも対応し、通常のauthoringで作ったListFold集計を手書きrunnerなしで実行した。空Listにも要素型を明示し、型不正や過大な入力は実行前に拒否する。さらにスカラーのOption入出力・期待値に対応し、NoneとSome(0)/Some(false)を区別し、空入力でNoneを返すfold関数の実行を確認した。スカラー型同士のResultも入出力・期待値に対応し、両側の型とOk/Errを保持する。通常のErrをホスト例外やprotocol失敗と区別し、authoringした条件分岐の両側を共通runnerで実行した。続いてスカラーfieldを持つProductの入出力・期待値に対応した。TypeId/FieldIdで全fieldを検査し、引数順ではなくfield identityで構築・比較する。空Product、field access、構築、Int64境界値、表示名が曖昧なfield、fieldの過不足・型不正の拒否を検証した。スカラーfield／fieldなしvariantからなるUnionも入出力・期待値に対応し、TypeId/VariantId/FieldIdの所有関係とfield型を検査する。Union構築・MatchUnion・variant違いの不一致を通常runnerで確認した。さらにList/Optionを外側に重ねる入れ子に対応し、List<Option<Int64>>、Option<List<Option<String>>>、List<Product>、Option<Union>、List<Result>を検証した。値変換に深さ・合計node数・合計文字列量の制限を設けた。続いてProduct/Unionのfield内部とResultのpayload内部の入れ子にも対応した。fieldは最上位と同じ型付き値形式で再帰的に検査し、両側がスカラーのResultは従来の平坦形式、片側でも非スカラーならTypeRefを持つ入れ子形式に限定した。深さはList/Option/Result/Product/Unionの合成層を経路ごとに8層までとし、再帰的な名前付き型は結果型・container要素型として実行前に拒否する。`List<String>`や`Option<Int64>`をfieldに持つProduct、Productの一覧をfieldに持つUnion、Productやスカラーを包むResult、8段のProduct連鎖を往復させ、field accessとResult構築を含む関数の実行・深い位置の不一致を確認した。続いて再帰的な名前付き型の値にも対応した。型の検査では現在の経路上の名前付き型への再参照を再帰として受け入れ、非循環経路だけに8層の制限を課す。生成runnerは名前付き結果型ごとに1つの変換関数を生成するため、再帰型でもコードは有限で、値は経路ごとに32層までに制限する（入力は実行前、出力は生成runner内で拒否）。`Node { next: Option<Node> }`の連鎖と`List<Tree>`を持つUnionの木を往復させ、再帰値を構築する関数と32層を超える結果の拒否を確認した。さらに診断をsemantic identityへ戻せるようにした。期待値との不一致ではField（TypeId/FieldId）・List index・Some/Ok/Errの経路で最初の差分と両側の部分値を返す。実行中の例外は生成シンボルをsymbol manifestで逆引きし、FunctionId/CapabilityOperationIdの呼び出し列として`RunRuntimeFailure`に含める。捕捉できないstack overflowも実行時のクラッシュ報告から同様に復元し、自己再帰と相互再帰の関数で確認した。変換上限の超過は従来通りprocess診断として区別する。最後に外部binding付きの実行にも対応した。Capability型の引数に、operationごとの戻り値・例外を順に返すfakeを指定でき、runnerはすべてのoperationをExternalImplementationとしてC#を生成し、fakeクラスを生成してその呼び出しをParameterId/CapabilityOperationId/CapabilityParameterIdで記録する。応答の枯渇は`RunBindingExhausted`、fakeの例外は`RunRuntimeFailure`として、operationと呼び出し元関数のframeを伴って返す。Unit値にも対応し、Product引数とResult戻り値を持つlookup、Unitを返すoperationの2回呼び出しを実行した。これまで既存のextended testで手書きしていたfake providerは、共通runnerの要求だけで置き換えられる。実サービスへの接続、`failureType`付きoperation、Float64値はまだ扱わない（並行実行は後続のG4で`ParallelAll`とfakeの`awaitCancellation`により対応した）。これによりT3の完了条件（通常ツールで作ったプログラムをリポジトリ所有の単一入口から検証し、失敗時にsemantic要素と期待・実結果を取得でき、生成ソースを修正しない）を満たす。残る非対応は§3のT3に記録する。

T1の最初の段階では既存のreviewed transactionへ`addProductField`と`deleteFunction`を追加した。Product field追加はすべての構築箇所に型付き既存値を明示して一括修正し、未指定や型不正は公開しない。不要関数はDirectCall/List callback参照・obligationがなくなった後だけ削除できる。既存identityの保持、review、JSONL経由のcommitと再オープンを確認した。次の小さな段階では`removeProductField`も追加し、field accessを事前に修正した後に宣言と構築値を一括除去する。効果を宣言する関数内の構築値は、評価が失われる可能性があるため保守的に拒否する。次の段階では未参照のProduct/Union宣言を`deleteType`で除去する経路を追加し、不要関数の削除と同じreview/publicationに合成できることを確認した。型、関数、capability、構築ノードに参照が残る場合はvalidatorで公開前に拒否する。さらに参照を除去したEffectとCapability宣言も削除できるようにし、関数→Capability→Effectの順でひとつのreviewにまとめ、操作・引数の削除IDと保存・再オープンを確認した。その後、関数の戻り値型変更を`simulateResultTypeMigration`として追加した。引数型移行と共通の内部機構（一時関数によるrecipeの具体化、全直接呼び出し元の差し替え、単一候補の検証・公開）に整理し、両方の移行でOption/Result/Bind/List等を含む全recipe種別を扱えるようにした。`lookup : String -> String`を`Option<String>`へ変更し、呼び出し元を`MatchOption`で書き換える変更をJSONLでreview/commitし、FunctionId・ParameterIdを保持したまま共通runnerで新しい振る舞いを確認した。続いてfieldを持つvariantの追加として`simulateAddUnionVariantLike`を追加した。既存variantのfieldを新しいIDで複製し、追加fieldを加え、全関数・入れ子のmatch armを新しいnode/binding IDで複製して網羅性を保ったまま1回で公開する。追加fieldは束縛のみ行い、armの本体は新IDが確定した後にtargeted replacementで変更する（将来IDの推測は行わない）。複製したarmを通じて新variantの値を共通runnerで実行した。最後に既存Product fieldの型変更として`simulateProductFieldTypeMigration`を追加した。Productを構築または当該fieldを参照するすべての関数の置換本体を必須とし、各本体を型変更後の宣言に対して束縛・型検査したうえで候補全体を一度だけ検証・公開する。`quantity: Int64`を`Option<Int64>`へ変更し、構築・表示・集計の3関数をJSONLでreview/commitして、TypeId・FieldId・関数/引数IDを保持したまま共通runnerで新しい振る舞いを確認した。これにより、field追加・削除・型変更、戻り値型・引数型の移行、fieldを持つvariantの追加、不要宣言の削除を通常ツールだけで、未修正参照や型不正を公開せずに行えるようになり、T1の完了条件を満たす。Union variantのfield型変更、参照元の自動書き換え、汎用migration DSLは引き続き対象外。

## 3. 実装目標一覧

P0は最初に着手する候補、P1はその後のまとまった実装目標を表す。全項目を一括実装する指示ではない。2026-09-25時点で全目標が完了条件を満たした（PR #215でマージ）。「計画時の主な制限」は策定時点の記述であり、各目標の実装内容と対象外は§2の記録にある。

| ID | 優先 | 目標 | 計画時の主な制限 | 状態 |
| --- | --- | --- | --- | --- |
| G1 | P0 | 実用的なデータ操作と反復 | Listの集計・要素取得・文脈付きcallback、文字列の基本操作が不足 | 完了 |
| G2 | P1 | 関数・型・ライブラリの再利用性 | callbackは既存の純粋な単項関数参照。一般的な関数値・closure・ユーザー定義型パラメーターがない | 完了（関数値・closureは対象外。次のキューへ） |
| G3 | P0 | 汎用的な外部連携 | Capability宣言はあるが、C# bindingがConsole専用。一般の戻り値・外部値・失敗を接続する経路が不足 | 完了 |
| G4 | P1 | 非同期・並行処理とリソース寿命 | 明示的な非同期合成・待機・停止・後始末の表現と実行契約が未整備 | 完了 |
| T1 | P0 | 作成したプログラムを変更・削除し続ける能力 | スキーマ・シグネチャ移行、宣言削除が限定的 | 完了 |
| T2 | P1 | 依存関係とプログラムの組み立て | mainは単一・一段の依存。既存PRを踏まえた明示的更新と合成が必要 | 完了 |
| T3 | 横断 | 共通の実行・テスト・診断経路 | 既存生成・実行機構を、アプリごとの手書き接着コードに依存しない開発経路へまとめる | 完了 |

### G1. データ操作と反復

**確認した制限:** List操作は構築、長さ、追加、連結、純粋なMap/Filter/Any/Allが中心。fold、要素取得・分解、検索結果の取り出し、実行時の文脈を渡すcallbackが不足する。文字列ノードはリテラル、空判定、包含判定であり、等値比較、結合、分割、数値との変換等を一般のプログラムで使う経路を補う必要がある。[S1][S2]

rrawrletの`team.py`には、文字列IDの比較、usage項目のキーごとの集計、文字列の切り詰め、整数除算等がある。これを「usage専用機能」ではなく、一般の集計・検索・文字列操作の要求に還元する。[R1]

**最小の開始点:** 蓄積値を受け渡すfold相当、型付きの要素取得、文脈を渡すcallback、および最初の実例に必要な文字列等値・変換を選ぶ。Map/Set、ソート、文字列処理一式は、ライブラリで実装できる部分とプリミティブが必要な部分を分けて追加する。整数除算・変換・overflowの扱いを導入する場合は意味を明示し、backendの既定動作へ任せない。

反復については、純粋な集計だけでなく、状態を更新しながら条件を満たすまで処理する表現と、効果を順序通り繰り返す表現も対象とする。`while`、loop-carried state、再帰、effectful traversalのすべてを一度に追加するのではなく、必要な処理を表せる最小の整合的な方式を選ぶ。

**注意:** 現在loopノードはないが、`DirectCall`があることから、再帰の可否をノード一覧だけで断定しない。自己・相互再帰のauthoring、検証、lowering、スタック使用の保証は着手時に確認する。既存方式で満たせれば不足扱いを取り下げる。Boolのnot/and/orも条件分岐で表現可能なので、専用ノードの不在を表現不能と数えない。

**完了条件:** 任意長の入力を集計し、実行時のキーで検索し、蓄積状態を更新する処理を通常ツールで作成・保存・実行できる。空入力、要素なし、入力順、型不一致のうち、その操作の重要な契約を小さく確認する。アプリ固有のhost関数に集計を移さない。

### G2. 関数と型の抽象化

**確認した制限:** 現在の高階List操作は既存の純粋な単項関数をFunctionIdで参照する。`TypeRef`はprimitive、named、capability、組み込みのOption/Result/Listであり、ユーザー定義の型パラメーターや関数型は持たない。[S1][S2]

**目標:** 文脈を伴う関数の再利用、共通処理の組み立て、型の異なるデータへのライブラリ適用を可能にする。最初は明示的な文脈引数でもよい。一般的なclosureや型パラメーターを導入する際は、capture、scope、型引数の置換、効果の扱い、stable identityを仕様化する。高階型、型クラス、暗黙の型推論体系まで一括で要求しない。

**完了条件:** 同じ処理を異なる文脈へ適用でき、選択した型パラメーターの範囲では別の具体型へ適用しても関数本体の複製が不要になる。通常のauthoringから参照・変更・型エラー確認まで行える。G1の文脈付きcallbackと別の競合する方式を作らない。

### G3. 汎用的な外部連携

**確認した制限:** C# backendのbinding種別は`SystemConsoleWriteLine`だけで、対応するシグネチャも限定されている。`FailureType`の指定には`UnsupportedCapabilityFailure`がある。これはCapabilityやResultが存在しない問題ではなく、宣言から一般的な外部実装へ接続する経路の不足である。[S5]

**目標:** アプリに依存しない、型付きの外部操作宣言と実装bindingの仕組みを整える。外部ライブラリの追加のたびに、コアへ製品名・API名の特殊ケースを増やさずに接続できることを目指す。

引数・戻り値、Product/Union等の変換、実装参照の解決、外部オブジェクトの受け渡しを必要な範囲で定義する。外部ハンドルは現在のCapability値で表現できるかを先に確認し、不必要に新しい型体系を重ねない。バイナリやopaque value等も、必要なデータ契約として検討し、特定API向けの万能な文字列IDへ潰さない。

通常の失敗はResult等で表現できる。`FailureType`との関係、host例外をどこでどう変換するか、未対応のbindingをどの段階で診断するかを明示する。期待される失敗、意味論の不正、backend非対応を混同しない。外部処理が純粋であることも暗黙に仮定しない。[S6]

**完了条件:** 別モジュールの小さな外部関数を明示的に登録し、複合値の受け渡しと型付き失敗を通常ツールから記述したプログラムで実行できる。bindingを追加してもコアの操作一覧変更は不要。生成コードの手修正や、アプリの判断・制御全体を隠すhost関数を必要としない。

この段階でCodex、Git、HTTP、ファイルシステム等の実装をNyrwvy本体に追加する必要はない。fakeの外部実装で連携契約を確認できる。

### G4. 非同期・並行処理とリソース寿命

**確認した制限:** 現在の`Bind`は順序付けと値の再利用を表すが、汎用的なtask/await、非同期ストリーム、キャンセル、非同期の後始末を直接表す型・ノードはない。独立した副作用をグラフに並べることも、並列実行の指定にはならない。[S1][S2]

**目標:** 待機、複数処理の合成、結果の受け取り、停止要求、終了確認、スコープ終了時の後始末をプログラムとして記述できるようにする。具体的な表現は言語構成、型付きライブラリ、backend runtimeのどこが担うかを決める。専用schedulerやOS機能の再実装を自動的に要求しない。

後始末は非同期処理専用ではなく、成功・失敗など複数の出口を持つ一般的なリソース利用にも適用する。キャンセル要求と終了を区別し、終了前にどの処理を待つか、失敗と後始末の結果をどう保つかを明示する。所有権・借用・共有可変状態の言語体系まで先に導入する必要はない。[S7]

**完了条件:** 2つの独立したfake処理を開始し、待機・片方の失敗または停止・残った処理の終了・後始末を、選択した契約に従ってNyrwvy側から記述できる。ホスト言語の一枚岩の関数へ制御全体を移さない。実プロセスやネットワークの確認は、実際にその外部ライブラリを接続する境界まで要求しない。

### T1. プログラムの変更・削除

**確認した制限:** 作成、部分式置換、名前変更、引数追加、明示的なeffect変更、限定的な引数型移行、fieldless variant追加はある。一方、Productのfield追加・変更・削除、関数の戻り値型変更、fieldを持つvariantの変更、不要な宣言の削除などは、現在の維持された操作だけでは広く扱えない。[S1][S3]

**目標:** 宣言や関数を増やすだけでなく、実際の変更要求に合わせて改修・削除できること。変更対象は型・field・variant・関数・引数・Effect・Capability/Operationを見渡し、最初の具体的な変更で必要な範囲から整える。

影響する構築・参照・呼び出し・matchを発見し、必要な修正を1つのreview/publicationにまとめる。既存IDを意味の継続に応じて保持し、新規要素のIDはシステムが割り当てる。削除では参照が残ったまま通さず、必要な修正または適切なobligationを明示する。既存のtransaction機構を優先して使い、万能なmigration DSLを前提としない。

**最小の開始点:** Productのfield追加とその利用箇所修正を選び、次に戻り値型変更または不要関数の削除へ進む。各変更を別々のアプリ専用操作にせず、共通する型付き編集の仕組みに寄せる。

**完了条件:** 現実的なスキーマ変更を通常ツールのみで実施し、必要な影響範囲をreviewして、未修正参照や型不正を公開せずcommitできる。保存・再オープン後も変更していない意味要素のidentityが維持される。

新しい言語機能は内部APIに実装しただけでは完了しない。作成・inspect・変更に必要な機能を共有ツール層/MCPにも公開する。ただし、機能ごとに独立したtop-levelツールを増やすことまでは要求しない。

### T2. 依存関係とプログラムの組み立て

**確認した制限:** mainの依存は単一・一段のpinned snapshot。通常ツールからの依存指定と同じ版の内容保持はPR #214に実装済みなので、まずその作業を再利用する。[S3][S4]

**目標:** 複数のライブラリ、ライブラリが参照する別ライブラリ、明示的な依存先更新を扱えること。公開する宣言、参照先の解決、同名・ID衝突、重複する依存、更新による影響を扱うために必要なmodule/packageの最小契約を決める。

旧版をそのまま再現する操作と、新しい版へ更新する操作は分ける。後者は差分と影響を示して再検証・reviewする。大規模なregistry、オンライン配布、汎用semantic mergeは、この目標の前提ではない。[S7]

**完了条件:** 小さなプログラムが複数・多段の依存を参照でき、独立したライブラリの判断ロジックをコピーせず再利用できる。意図した版を再現し、明示的な更新で利用側の型・effectへの影響を確認できる。既存PRで完了する範囲をもう一度実装しない。

### T3. 実行・テスト・診断の共通経路

**現状:** 生成・実行の仕組み、Function Focus、Change Review、シンボルmanifestは既にある。「実行不能」「可視化がない」とは扱わない。一方、言語の表現力が増えても、個別プログラムごとの手書きrunnerや生成コードの識別子調査が常に必要では、直接ソースを扱わない開発経路が途切れる。[S3][S5]

**目標:** entry point、型付き入力、必要なbinding/依存、結果を指定してbuild/run/testする共通経路を整える。純粋関数の入出力例やfake bindingで、利用者のプログラムを検証できるようにする。コンパイラー自身のテストと、利用者のプログラムの振る舞いのテストは区別する。

診断は型・関数・ノード・外部操作等のsemantic identityへ戻れることを目指す。新しい構成はrecipe reference、focused inspect、impact/review、実行時診断から必要な情報を取得できるようにする。初手から完全なstep debugger、巨大なgraph viewer、新しいテスト言語を作る必要はない。

**完了条件:** 通常ツールで作った小さなプログラムを、リポジトリが所有する単一の実行・検証入口から検証できる。失敗時には対象のsemantic要素と期待・実結果を取得でき、生成ソースを修正せず原因箇所を調べられる。既存の入口を確認し、不足だけを足す。

**完了後に残る既知の非対応:** `runPure`は完了条件を満たすが、次の2点は扱わない。いずれも具体的な利用者が現れた時点で着手する。

- **Float64値:** 入出力の値形式にFloat64がない。Float64の引数は指定できず（`RunArgumentTypeMismatch`）、Float64を含む結果型は実行前に拒否する（`RunResultTypeUnsupported`）。Float64を引数・戻り値に持つoperationを含むCapabilityはfake・host bindingにも指定できない（`RunBindingTypeUnsupported`）。IEEE 754の値（NaN、符号付きゼロ、無限大）の表現と等値比較を決める必要がある。
- **`failureType`付きoperation:** C# backendが明示的に非対応のため、プログラム中のどこかにあれば実行全体が失敗する。Q-010の方針（具体的な利用者まで非対応）に従い、期待される失敗は`Result`の戻り値で表す。

## 4. 実装の進め方

**最初の具体的な対象はG1の集計・文脈付き検索。外部機能への汎用的な到達経路としてG3を次の主要目標にする。** T1は継続的な開発の前提なので、非同期や大きなライブラリ基盤の完成まで後回しにしない。

| 順序 | まとまり | 完了の見方 |
| --- | --- | --- |
| 1 | G1の小さなデータ処理と、必要最小限のT3 | 集計・実行時キー検索をauthoringから実行まで通す。必要な文脈引数はG2と共有する。 |
| 2 | G3の一般的なbinding | アプリ名に依存しない外部実装と複合値・失敗を往復させる。 |
| 3 | T1の最初のスキーマ変更と削除経路 | 新規作成したプログラムに仕様変更を加え、不要要素も除去できる。 |
| 4 | T2と必要なG2のライブラリ再利用 | 複数・多段依存と明示更新を扱い、必要な型抽象化を追加する。 |
| 5 | G4の非同期・寿命管理と残る反復表現 | 外部fake処理を組み合わせ、待機・停止・後始末を記述する。 |

**2026-09-25時点で上表の1–5はすべて完了した。** 以降の実装キューは本計画ではなく[post-M2 roadmap](post-m2-roadmap.md#immediate-execution-queue)で管理し、2026-09-25のrrawrlet sliceの調査結果と[Semantic independence](../concepts/semantic-independence.md#current-gaps)のギャップを対象にする。

T3は各段階で必要な細い実行・診断経路を先に整え、共通化の必要が具体化したところでまとめる。上表は依存関係を踏まえた推奨順であり、別の具体的なblockerが先に出た場合は理由を記録して入れ替える。全目標を達成するまでrrawrletの全面移植を始められない、という意味ではない。移植の可否と範囲は別途判断する。

各実装issueでは、対象処理、現在の制限、汎用機能への還元、最小の変更範囲、意味論・identity・reviewの不変条件、完了条件を記録する。コア構成とライブラリ実装のどちらが適切かを決めてから機能名を固定する。既存のM3-M5の完了記録を未完了へ戻さず、M6のscale/collaboration目標とも混同しない。

## 5. 完了判断と検証

新しい機能は、該当する範囲で「authoring → 型・効果等の検証 → review/commit → 保存・再オープン → lowering/backend → 実行結果・診断」を通せたかで判断する。保存形式やprotocolを変える場合は互換性・移行の扱いを明記する。

[Verification philosophy](../developer/verification.md)に従い、fastには重要な型契約・境界・状態遷移・regressionだけを置く。複数層をまたぐ代表的な確認は機能の区切りでextendedとして実施し、実OS・credential・外部サービスの検証はその境界を変えたときだけ行う。ノードや分岐を足したこと自体を理由にテストを増やさない。

rrawrletの`summarize`、外部呼び出し、並行処理等は代表例として使えるが、Nyrwvyのcompletion criteriaをrrawrlet専用の操作へ置き換えない。既存のlive authoring成功記録は再利用し、同じモデル評価を反復しない。手動確認が必要になった場合だけ、実機でしか確かめられない境界を明示し、単一コマンドと共有可能な結果へまとめる。

## 6. 今回の計画に含めないもの

Codex/OpenCode/Git等のアダプター本体、rrawrletのUI・運用ポリシー・Apply仕様・実行データの永続化は、アプリまたはライブラリ側の仕事である。これらに必要な汎用能力はG1-G4/T2で扱うが、具体的な機能の内蔵は目標にしない。

snapshotの同時編集やsemantic merge、電源断耐久性・データベース化、完全な所有権体系、package registry、独自UI frameworkは、それぞれ別の具体的な要求が出た場合に検討する。今回のrrawrlet再構築という題材だけから、それらを必須要件にも不要の結論にも結び付けない。

## 7. 根拠と参照先

基準時点の事実は以下に固定する。未実装の目標は本計画の提案であり、現在の仕様や実行結果を表すものではない。PR #214でソースの所有ディレクトリが変更されるため、調査根拠のソースリンクはcommit固定とする。

- **[S1]** [Current language subset](https://github.com/kw4r3n/nyrwvy/blob/23a2df028dfbdda046382c26c00ee6d2a7794368/docs/reference/language-subset.md): List/callback、Bind、effect、既存編集操作の範囲。
- **[S2]** [Foundation.fs](https://github.com/kw4r3n/nyrwvy/blob/23a2df028dfbdda046382c26c00ee6d2a7794368/src/Nyrwvy.Core/Foundation.fs): PrimitiveType、TypeRef、SemanticTypeKind、SemanticNodeKind。
- **[S3]** [基準時点のREADME](https://github.com/kw4r3n/nyrwvy/blob/23a2df028dfbdda046382c26c00ee6d2a7794368/README.md)と[post-M2 roadmap](post-m2-roadmap.md): authoring、依存、M3-M5、実行経路の既存到達点。
- **[S4]** [PR #214](https://github.com/kw4r3n/nyrwvy/pull/214)と[確認したgraph-tools実装](https://github.com/kw4r3n/nyrwvy/blob/d94e536c60253ddee36ac6d11e2b5e81eb72dd26/tools/nyrwvy-agent/graph-tools.mjs): dependency指定とpin操作。本文の完了記録はPR報告であり、本調査でテストを再実行したものではない。
- **[S5]** [CSharpBackendModel.fs](https://github.com/kw4r3n/nyrwvy/blob/23a2df028dfbdda046382c26c00ee6d2a7794368/src/Nyrwvy.Core/CSharpBackendModel.fs)と[CSharpBackendValidation.fs](https://github.com/kw4r3n/nyrwvy/blob/23a2df028dfbdda046382c26c00ee6d2a7794368/src/Nyrwvy.Core/CSharpBackendValidation.fs): binding種別、互換シグネチャ、失敗診断、symbol manifest。
- **[S6]** [Failure and obligation policy](../specs/failure-obligation-policy.md): 意味論の不正、未解決事項、backend readiness、実行時の通常失敗の区別。
- **[S7]** [Open questions](../decisions/open-questions.md): Q-002、Q-010、Q-012等の未決事項。新しい方式はこれらを黙って決定済みにしない。
- **[R1]** rrawrlet `c4f9669b87d19b30b18acfc59ac01cb1ce846132`の[team.py](https://github.com/kw4r3n/rrawrlet/blob/c4f9669b87d19b30b18acfc59ac01cb1ce846132/rrawrlet/team.py#L1-L130)と[README](https://github.com/kw4r3n/rrawrlet/blob/c4f9669b87d19b30b18acfc59ac01cb1ce846132/README.md): 具体的なデータ操作、外部処理、対話・並行実行の題材。
