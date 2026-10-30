# Codex 中止與恢復介面驗證

驗證日期：2026-10-24～2026-10-27。此文件先說明 v1.5.1 的額度重置等待與 v1.5.0 的一次性桌面續聊，再保留各版桌面控制、VS Code 整合及早期研究原型紀錄。各項結果只適用於所列版本與範圍；原型成功不能代替正式原生 Host 的驗收，真實訂閱帳號的額度耗盡與完整自動切換仍未驗收。

## v1.5.3：代理啟動與可選功能的版本檢查

26.1027.3384.0 套件升級後，發現 Relay.ValidateLaunch 無條件呼叫 Local Work 封鎖的固定 ASAR 雜湊檢查，早於啟動預檢轉送即會失敗。改為只解析並核對原套件後端與非遞迴目標；既有 Host／App／Relay 身分檢查不變。Host 在啟用 Local Work 封鎖時仍核對雜湊，DesktopConversationClient 的進階續聊版本檢查也維持。合成新版套件回歸涵蓋後端解析、遞迴拒絕、後端缺失拒絕及 Local Work 未驗證版本拒絕。未啟動真實 App；另一台的完整啟動仍需使用者更新後確認。

## v1.5.2：新電腦啟動與錯誤分類

2026-10-30：修正啟動設定以 UTF-8 明確解碼；背景測試執行真正 PowerShell 啟動腳本的中繼資料讀取前段，模擬舊文字編碼，確認舊寫法破壞非英文路徑、新寫法保留所有欄位。測試在套件查詢與啟動前停止，不操作真正 App。

私人資料夾及原子暫存檔在建立時明確指定目前 Windows 使用者為擁有者；保留對既有不同擁有者、寬鬆權限與連結的拒絕。錯誤診斷只記錄白名單分類、來源與代碼，不記錄原始訊息、路徑或帳號內容。此修正處理已重現的相容性缺陷；另一台電腦畫面的通用 IOException 尚不能證明其實際觸發原因，仍需新版錯誤代碼確認。

## v1.5.1：等待額度重置與同帳號恢復

只有目前帳號的最新額度確認耗盡、沒有其他已確認可用帳號，且受管後端擷取的唯一原回合為 usageLimitExceeded 失敗時，才建立 WaitingForQuota 記錄。額度為零本身不會產生續跑意圖，也不會中止仍在執行的回合。等待不持有桌面送出閘門、不關閉 App；每 60 秒查詢，從目前帳號之前的手動順位與目前帳號選出第一個新鮮且可用者。只提供每週額度的帳號可用；過期、未知、到重置時間但尚未重新查證的資料不可用。

新等待記錄沿用 DPAPI、私人 ACL、原回合跨記錄去重與全域 mutex，使用 Version 2 與舊記錄區別。尚未領取的單筆等待可在切換器重啟後恢復；Captured 之後的狀態一律不重建自動送出。正常關閉切換器會保留純等待，即使正逢查詢額度也一致；停用監看、桌面續聊或手動帳號操作則取消等待。純等待的取消在磁碟仍為 WaitingForQuota 時才生效，不能誤取消另一個切換器已領取的操作。

恢復前，在等待其他程序前後各重新擷取一次原對話，逐一核對 session、後端代次、thread、turn、模型、權限、路徑與前端設定。手動續聊、設定或連線變更會取消舊等待。帳號與額度也在領取前、程序關閉後及套用前再次查證。領取目標在 mutex 內先落盤，再綁定桌面恢復；接續既有正常關閉、重開、驗證及 Sending 先落盤的至多一次流程。同帳號恢復不呼叫登入寫入，仍正常重開受管 App，以沿用新後端代次與完整原對話驗證。

背景測試使用合成帳號與假程序，包含較高順位、同帳號、同時重置、單帳號、每週額度、未知與過期資料、排序／登入／額度競態、等待重啟、競爭領取、取消、手動新回合，以及 ACK 遺失後重啟不重送。整合測試實際組合 Controller、Workflow 與加密記錄，僅帳號與桌面傳輸使用替身。本次沒有啟動 Codex 視窗，尚未新增實際 App 的額度重置續跑驗收；v1.5.0 的實機限制仍適用。

2026-10-27 發佈前驗證：從只含來源的 Git 複本建置後，457 項 C# 背景測試與 9 項 VS Code companion 合成測試全部通過。另使用中文、空白路徑及不同工作目錄，驗證啟動器 `--build-only` 的首次建置、完整快取、缺少相依檔重建、無效參數拒絕及失敗時不等待按鍵；10 個發行檔與 VSIX 的 5 個項目完整。這輪不啟動真正 Codex 視窗，也不停止現有對話。此結果確認來源複本不依賴原機的建置產物，並非另一台實體 Windows 電腦或真實帳號耗盡後的端到端驗收。

## v1.5.0：桌面額度切換與一次性續聊

自動監看先驗證目前帳號已耗盡，按保存的手動順位選取新鮮且可用的候選者。續聊僅在桌面控制與續聊選項都開啟時啟用；手動切換仍等待閒置，不自動送出訊息。工作擷取只選單一本機持久主對話，或單一最後因 usageLimitExceeded 失敗的載入對話；不猜測多個主對話、遠端環境、暫存對話或活動目標。

正常中止透過 App 自己的對話管理器，綁定實際擁有者與原回合識別。等待其他程式結束後、真正中止前，重新取得來源與候選帳號額度，並核對登入、順位、資料新鮮度與重設時間；來源已恢復額度或狀態改變時，不中止原回合或套用帳號。之後核對回合已終止、待處理請求與背景工作清空，沿用 Host 兩階段正常退出、後端 EOF／exit 0、App 非強制 Restart Manager 與最後程序安靜檢查。帳號套用前後都保留來源身分、額度與順位檢查；未受管的 VS Code 或其他程序仍阻擋切換。

新版 App 使用同資料夾的原生 DesktopAppRelay：App 的普通本機 CLI transport 啟動此固定代理，代理由私有環境鍵取得受管 Host 的本機 WebSocket，Host 再持有真正的 Codex 後端。這讓桌面保留本機對話 IPC，而不繞過 Host 的程序歸屬、工作狀態或正常退出控制。代理驗證 session 私人記錄、目前使用者／Windows session、App 父程序、Host、固定程式路徑與 loopback TCP 擁有者。不對 App 設定公開 WS override 或 FORCE_CLI，以保留原有 hosted／SSH 選擇。App 的 IPC 與 Local Work 封鎖均綁定已驗證的安裝 ASAR；未知版本停止自動操作。

同一 App 可能建立獨立的帳號與對話連線；每條連線各自持有受管後端，共用同一送出閘門。只有前端成功載入原對話的回覆可證明對話後端歸屬；控制器自己的中繼查詢不算載入證據。每個成員都必須完成正常退出準備，才能對任何後端送 EOF。需要 App 工具的對話另驗證命名管道伺服器的原生 App 身分，管道地址不寫入磁碟記錄。

實際中止後的 CLI 曾在處理器結束後仍清理執行環境，保留的原生 handle 證明 EOF 後 43.743 秒才以 0 退出。最新版改為整組共用 90 秒單調時計期限：只提交一次 EOF，之後每次最多一秒確認同一保留程序，同時重驗授權、呼叫者與取消狀態；非零退出立即拒絕，程序消失不算退出成功。客戶端期限為 120 秒，以容納正常 App 關閉。凍結後的前端普通請求在 admission 鎖外即拒絕，避免阻住後續清理回覆；仍保留鎖內重驗。

切換後使用同一 Home、桌面 profile 與 SQLite 位置開啟原 thread URI。冷啟動的 URI 若未載入對話，Host 在目標登入、原生 App、唯一工具管道與所有後端閒置都核對後，透過同 App 的既有 navigate_to_codex_page 導航原對話；不使用全域 URI handler，也不提供通用工具呼叫。已驗證的 App 會在路由接收器註冊前丟棄此導航訊息，工具成功只代表發出訊息。因此只對同 App／同操作／同原對話允許最多八次、至少間隔兩秒的冪等導航；每次重新驗證控制權，發生工具錯誤或逾時即停止。導航不建立對話或啟動回合，取得真正前端的 thread/resume 成功回覆後立即停止導航，才進入帳號、設定與續聊核對。訊息送出不使用此重試機制。

之後確認新後端世代、登入、原回合尾端、模型、工作目錄、持久化對話檔案、執行資料夾集合及權限一致。只有完成 App 對話管理器的擁有者探索與載入後，才保留一次性送出權並要求送出固定文字「繼續執行」。新後端在 App 啟動前即安裝 admission barrier，阻擋競爭的新回合、steer、登入或設定異動；保留必要唯讀載入及匹配的原對話恢復。前端摘要只接受已驗證 App 的有限設定，包括五個精確的布林功能鍵；不接受任意 features 或其他 override，恢復時仍逐值比對。

本機票券以 DPAPI 加密、私人 ACL、嚴格格式及跨程序 mutex 保存，先持久寫入 Sending 才送出要求。Bridge 在轉送前消耗相同操作的權利，同一識別即使取消也不能再次接納。前端 ACK 遺失時只讀取 Host 已觀察的相同新回合回覆，不重新送出；切換器重啟後只釋放匹配的舊閘門，不自動重送。這提供至多一次送出嘗試；不能把未收到 ACK 等同未送出，也不能承諾網路失敗時恰好執行一次。

驗證使用假帳號、隔離設定檔及本機合成服務，不使用真實登入，也不操作使用者目前的 App。各項最終結果列於本節完成紀錄；測試範圍不等同真實訂閱帳號的跨帳號完整驗收。

完成紀錄：

完整 C# 回歸 399／399 通過，包含跨程序送出權、實際正規化欄位、回覆遺失、續聊後控制釋放失敗、重開後釋放成功／取消票券、通知阻擋、原回合在中止前已因額度失敗的競態核對、等待其他程式期間的額度重設、有界冪等對話導航、Windows extended-drive 對話檔案等價、時鐘倒退，以及有界正常退出與退役操作的冪等釋放。

- 真正桌面 App 使用本機合成模型與兩個不同假帳號，已驗證來源對話的擁有者、活動回合擷取、正常中止 ACK 與同回合的權威 terminal 狀態。另一次閒置來源由完整 Host transaction 自動關閉成功。
- 獨立目標階段的實機驗證通過：沿用同一 Host 與已擷取的原對話，在手動正常關閉來源 App 後，套用不同假帳號、重開同對話，通過身份、模型、權限與尾回合核對，再持久保存 Sending。目標模型請求從 11 增至 12，恰好一次「繼續執行」，新回合 receipt 相符；重複呼叫被拒，重新開啟票券儲存也沒有新增請求。目標完成後的自動 EOF／App 正常退出成功，當輪 18 個原有使用者程序身分保持不變。此結果包含手動來源回復，不能當作完整自動來源至目標串接通過，也不等同 WPF GUI 已實際重新啟動。
- 最新 90 秒版本已通過背景測試，包含 25 秒執行環境收尾、取消、撤權、非零退出，以及真實命名管道／WebSocket 的凍結清理反例。最新實機回合亦通過活動來源擷取、正常中止與權威 terminal、完整自動來源 EOF／App 退出、目標套用與恢復、原對話載入及完整 preflight。目標 Start 有 ACK、送出閘門已 claimed 且觀察到新 turn receipt，但本機模型服務只觀察到來源請求從 13 增至 14，未觀察到目標模型請求，因此不能把這輪記為完整自動續跑成功。這與前一個手動來源回復後的目標成功案例分開計算。使用者要求停止另開 Codex 視窗後，已停止所有實機測試並正常關閉隔離 App；完整自動續跑仍待驗收。

- 早期單後端傳輸階段的真正桌面 App 隔離測試 14／14 通過（不涵蓋帳號輔助後端、renderer 或原對話載入）：初次啟動、可信 Relay、可靠閒置、OS 身分綁定的 App IPC 初始化、後端 EOF／exit 0、App 非強制關閉／exit 0、全新 App／Relay 恢復、再次 IPC 與正常退出。21 個原有使用者程序身分均保持存活；未建立 auth。App 的八次外部請求由本機拒絕 proxy 擋下。測試共享產品來源，僅在 QA 複本把「全機沒有其他 App」的檢查限制為自身隔離 profile，並由獨立 fixture Main 提供隔離啟動清單；程序身分、Relay、後端與原生關閉檢查維持。
- 真正隨附 CLI 0.164.0-alpha.11.1 在隔離 Home 與本機合成 Responses 服務，驗證活動回合可讀取有效設定、itemsView=notLoaded、正常中止、EOF／exit 0、同 thread 恢復、原歷史保留及一個新的續聊回合。單純 thread/resume 不會產生模型請求；新 turn/start 才會。重用 clientUserMessageId 在同程序或重啟後都會再執行，因此產品另外保留不可重試票券與轉送端閘門。
- 三個跨元件情境均通過：正常續聊、前端 ACK 遺失但 Bridge 有匹配回覆、ACK 不確定後重建切換器控制器。每個情境使用實際 controller、workflow、DPAPI、WebSocket、Bridge、正常退出交易、全新後端世代及假登入套用，模型後端都只收到一次 exact「繼續執行」；重複 tick、同操作重播及模擬 GUI 重啟都不會再送。此處桌面 follower 是模擬傳輸，不能代替真正 App 送出介面的驗證。
- 從已鎖定 App bundle 擷取真正的 request 正規化函數，兩種權限模式的實際輸出均通過 admission，明確保留原政策；只接納固定已知預設欄位、受限的 App 時間與 Git 唯讀上下文。未知 MCP 搜尋政策或其他 override 停止自動送出。
- 另擷取真正的 cold-resume builder，驗證 history 只能為 null、path 必須匹配後端驗證的原持久化對話檔案、固定分頁讀取及權限請求工具旗標；這些實際載入欄位也經過跨元件 Bridge 測試。執行資料夾集合取自 authenticated thread/resume，恢復時必須相同；真正 CLI 的 readOnly 測試證明保留資料夾後仍為 readOnly/networkAccess:false，不增加寫入權限。
- 主視窗及設定視窗 37 項畫面外檢查通過，使用假帳號渲染；預設尺寸可顯示新增選項與兩個帳號，縮小時帳號清單可捲動，底部操作不重疊。VS Code companion 的九項合成測試通過。

## v1.4.8：控制偏好保存與自動接續啟動

私人設定新增可選的 ControlRequested，與 Enabled 控制授權分開。未保存開關選擇的新設定、或沒有授權紀錄的舊設定，預設希望開啟桌面控制；舊有已授權且停用的狀態沿用關閉。若已有明確的 ControlRequested，則一律沿用使用者選擇。選擇會持久保存，重新開啟切換器時沿用，不因當下尚未連線或正在等待正常退出而改寫為關閉。錯誤型別、無法讀取或未知設定不得當成新設定套用開啟預設，也不建立控制授權。

主畫面與桌面控制視窗分別呈現開關選擇和實際連線／工作狀態。ControlRequested=true 不能證明已授權、TransportReady、Known 或 Idle；使用者關閉後，停止自動啟動並撤銷控制，現有 App 可繼續正常運作，之後重新開啟仍保持關閉。

切換器重新開啟時，依保存的開關進行唯讀狀態確認：可驗證的既有受管連線直接沿用；未受管 App、舊版控制或其他候選程序仍存在時持續等待正常退出。除候選程序外，記錄中的活 actor 也必須確實退出，才可進入原有首次啟用的穩定退出檢查、建立授權並啟動受管桌面。等待不能強制關閉既有 App，也不能為取得連線另開第二個實例。

自動接續啟動不會套用帳號或重送聊天。帳號切換與恢復仍保留原有 Known／Idle、活動回合、待處理要求、背景工作、身分核對、正常退出與最後程序檢查；Local Work 封鎖仍須通過相容性驗證。此行為調整不表示真實跨帳號登入、原聊天畫面恢復或自動續聊已完成驗收。

最終 C# 回歸 264／264、companion 合成測試 9／9 通過。新增測試涵蓋開關跨實例保存、舊設定移轉、不授權的開啟預設、錯誤型別及非一般檔案拒絕、升級撤銷仍保留偏好、既有忙碌連線不啟動第二實例、獨立執行器／VS Code／孤立 actor 持續阻擋，以及身分查詢失敗不得推定退出。另以合成帳號於畫面外渲染主視窗的開啟／關閉兩種狀態及設定視窗，34／34 相關介面與啟動檢查通過；預設視窗未重疊或需要捲動。驗證未中止使用者 App，實際登入後的新版啟動仍需重新開啟切換器後確認。

## v1.4.7：桌面控制入口調整

桌面受管模式與 Local Work 選項移到主畫面的「桌面控制…」獨立入口，桌面狀態及程序來源標示同步移除實驗名稱。VS Code 使用另一個「VS Code 整合…」入口，仍屬可選實驗整合。歷史紀錄中的舊畫面名稱保留供比對。

此版只調整入口及顯示文字，沒有改變控制授權、程序歸屬、Known／Idle、正常退出或 Local Work 相容性檢查。版本標示與入口變更不能代替真實跨帳號切換、原聊天恢復及自動續聊驗收；本文件仍保留各項已驗證範圍與限制。

## v1.4.6：Local Work 獨立執行器與啟動選項

桌面 App 的 Local Work 可另外啟動本機執行器，與已受管的對話 app-server 分開。受管後端的 Known／Idle 不能證明這個獨立程序閒置，因此它仍須列為狀態未知的程序阻擋，不能僅憑父程序或同一 App 身分排除。

新增私人設定 BlockLocalWork，舊設定未提供時預設 false。整合視窗提供「受管桌面封鎖 Local Work（重新啟動後生效）」；儲存選項不等於授予自動控制，也不會操作現有 App 或執行器。設定只套用下一次受管啟動與恢復；一般啟動維持原行為。封鎖生效後 Local Work 無法使用，App 可能回報相關設定錯誤。無法核對受支援的安裝版本時停止受管啟動，不能在未知版本套用。

此選項不是 App 原生的永久停用設定。只在受管 App 的新程序環境指定 Local Work 專用 CLI override 為該 session 的私人目錄；目前已驗證的安裝碼會拒絕非檔案路徑，分類為不可重試的設定錯誤，並在遠端註冊及 exec-server 啟動前返回。單獨設定初始啟用旗標為 false 不足以阻止 renderer 後續授權。程式以安裝 ASAR 的完整 SHA256 與大小鎖定相容性，版本不同時拒絕套用，沒有修改安裝檔或全域環境。

完整回歸 C# 248／248、companion 合成測試 9／9 通過；實際安裝套件的相容性檢查通過。另從目前 ASAR 找到與本機研究副本逐 byte 相同的主程式 bundle，擷取原始路徑解析及 startOnce 函數，在無認證、無網路的隔離環境驗證九項條件：重複嘗試仍無遠端註冊、無子程序啟動、無排程重試，也不會退回預設 CLI。此結果驗證目前安裝碼的啟動分支，尚不代表已在使用者登入畫面完成重新啟動及帳號切換。

切換前仍共同檢查桌面、VS Code 與未受管程序；活動回合、待處理要求、背景工作、獨立執行器或任何未知狀態都會阻擋套用登入。選項變更不會自動關閉使用者目前的對話。真實跨帳號登入、原聊天畫面恢復與自動續聊仍未完成驗收。

## v1.4.5：桌面初始化回覆與連線就緒

已從安裝中的桌面 App 程式確認 `startAppServerProcess` 送出 initialize，成功回覆經 resolveInitialize／completeInitialization 更新連線狀態，沒有傳送 initialized 通知；委派的 onInitialized 也沒有傳送此通知。v1.4.3 加入的強制通知條件因此不相容，造成 App 可以載入、工作查詢卻永遠停在 initializing。本輪以這個實際握手模式取代合成測試原本總會發送通知的假設。

ManagedBridgeOptions.RequireInitializedNotification 預設仍為 true，桌面 Host 明確指定 false。成功 initialize 結果必須已轉送到前端，後端仍活著，才可建立 TransportReady；初始化錯誤、無效回覆及卡在前端 writer 的回覆均不能成為就緒。完整工作狀態仍需額外查詢，不由 TransportReady 推定 Known 或 Idle。

啟動穩定期使用相同 session／generation 的新鮮 TransportReady 回覆；世代改變會重新計時。程序篩選、共用 shutdown、prepare／commit、帳號套用與最後檢查仍要求原本的 Known／Idle 等條件。另將安全的狀態原因傳至桌面快照，並新增只執行 status 的同執行檔診斷入口；輸出僅投影狀態欄位，未知原因降為 other，不輸出 Home、Host、OwnedActors、PID、帳號或對話。

完整回歸 C# 237／237、companion 合成測試 9／9 通過，包含原生 WebSocket 前端僅送 initialize 的流程、回覆寫入尚未完成時不就緒、初始化拒絕不就緒、已連線但工作查詢未知仍不可 prepare／commit。真正隨附 CLI 在空白私人 CODEX_HOME、本機拒絕 proxy 且不送 initialized 的對照測試中，原必要通知條件為 TransportReady／Known／Idle 全 false、reason=initializing；新桌面條件三者皆 true。兩者正常 EOF／exit 0，未建立 auth，只有本機 proxy 要求，未操作使用者 App。

唯讀診斷入口也已實際執行：固定私人報告建立成功，只包含預定的 13 個狀態欄位，目前 App 前後均保持執行。因目前仍是前版協調器，此次診斷只將它保留為未受新版控制的來源，沒有停用或接管舊管線；真實登入後畫面的新版切換仍須更新整合後確認。

## v1.4.4：記錄讀寫競態與失效暫停證據

實際操作曾顯示記錄驗證失敗，但隨後逐筆使用交付版驗證器唯讀檢查時全部通過。私人壓力測試成功重現並行讀取與 File.Replace 的衝突：原讀取使用 FileInfo 路徑驗證再 File.ReadAllText，會出現 sharing violation、暫態 FileNotFound，以及 FileInfo.Exists 短暫失敗；讀者也會阻擋寫者替換。僅將 ReadAllText 改為共享串流仍不足，因為路徑驗證本身仍會競態。

新版 ReadPrivateText 使用 CreateFileW、共享讀寫／刪除及 OPEN_REPARSE_POINT 開啟葉端本身。同一個保留 Handle 驗證非 reparse／非目錄、最多 131072 bytes、目前使用者擁有者與私人 ACL，再讀取有上限的嚴格 UTF-8。只針對已列明的 Native 開啟暫態錯誤有限重試，最多 32 次、間隔 10ms；內容、格式、大小及安全驗證失敗不會被忽略或降為成功。整合設定與 launch specification 也使用同一讀取器。

另一個確定的 producer／validator 矛盾是內部交易仍為 quiesced，但新開啟 App 或晚到的失敗查詢使退出證據失效。公開快照現在降為 recoveryRequired，保留內部 EOF／恢復狀態。讀取歷史快照時，先驗證結構及身分，再僅將失去證據的 quiesced 狀態保守降級；Known／Idle 保持 false，活程序仍阻擋帳號套用，磁碟記錄不改寫。完整舊格式與缺欄位／錯型別的拒絕條件均有回歸。

最終讀取器另於 1ms 高頻替換條件下做兩組各四秒的私人測試：16 actors 寫入 1698／讀取 2971，192 actors 寫入 364／讀取 3602，讀寫錯誤均為零。格式錯誤、過大檔案、無效 UTF-8、過寬 ACL 與實際葉端 symlink 五項拒絕檢查通過。所有資料及 actor 均為合成，未寫入使用者整合資料或操作使用者程序。

完整回歸 C# 227／227、companion 合成測試 9／9 通過。使用最終編譯版本的 ReadSnapshot 唯讀檢查當時五份實際整合記錄，全部通過，目前 App 保持執行；此檢查沒有送出 quiesce／restore／disable 控制要求或改寫私人記錄。

## v1.4.3：啟動轉送與診斷

受管 App 的實際啟動日誌顯示後端握手成功後曾回覆後端不可用／切換中，之後本機帳號要求逾時。使用者也曾於啟動後按切換，因此切換中的拒絕本身不能單獨判為啟動根因；本輪只保留事件類型，未將日誌、帳號或對話內容加入專案。

找到並用對照測試重現 stdin 編碼缺陷：舊版使用 .NET Framework `Process.StandardInput` 的預設碼頁；強制父程序使用 ASCII 後，三項中文／emoji／組合字元／非 ASCII thread ID 測試均失敗。修正為同一個無 BOM、嚴格 UTF-8 writer 處理前端轉送、控制 RPC 與 EOF，三項測試通過。合成後端明確解碼 UTF-8，避免測試雙方使用相同錯誤碼頁而掩蓋問題。

初始化查詢現在同時要求成功 initialize 回覆與已轉送的 initialized 通知。新增合成驗證在暫不發出通知時查詢 status，確認未知、未送出 thread 查詢；通知完成後才成為可查詢狀態。Host 的定時／即時 status 查詢只在 admission lock 下捕捉成員，等待 RPC 時釋放該鎖；仍在同一鎖下驗證代次、啟動身分、凍結與兩階段切換。慢查詢測試確認查詢仍未完成時，前端可取得 admission；查詢失敗仍維持未知，未 prepare 或關閉後端。

Host 另保存無內容的可選 BackendExitCode 診斷；有退出碼時不得同時宣稱 known idle 或 quiesced。啟動只依新鮮控制回覆累計三秒可靠狀態，舊靜態快照只可用於回報失敗，不能當作已就緒。這項檢查證明後端連線穩定，不代表已驗證 Electron 畫面載入完成；介面已明確區分。未關閉使用者目前的一般 App，真實帳號的新版受管畫面仍須實際確認。

完整回歸 C# 220／220、companion 合成測試 9／9 通過。GUI 自測沒有 console handle 時仍執行 Unicode 往返，另用獨立 console 對照強制碼頁。實際隨附 CLI 0.164.0-alpha.11.1 也以空白私人 CODEX_HOME 與本機拒絕 proxy 驗證：舊版 writer 碼頁 950 的 ASCII 初始化／讀取通過，但中文字串設定寫入與讀回均失敗；新版 writer 碼頁 65001 的同一組寫入／讀回成功且內容完全一致。兩者後端均正常退出，未建立 auth，私人測試目錄已正常清理。這些結果可重現編碼缺陷，不能單獨證明使用者先前轉圈的唯一原因或真實帳號畫面已恢復。

## v1.4.2：協調器回收與舊版記錄

將受管 App 的退出證據與 Windows session 內所有同路徑 App 均已退出的條件分開。協調器清理只使用保留的受管 App 程序 Handle，全部確實退出後凍結 admission，才結束自己持有的後端輸入。後端須由橋接器保留的 Process 成功讀取退出碼，且橋接工作正常完成，才可回收協調器；查詢失敗、工作 fault 或仍有受管子程序均不能當成退出。即使後端退出碼非零，已無受管程序的協調器仍可自行退出，但不能將其狀態標為 known idle 或 quiesced。交易的正常 EOF0、App exit0、全域程序檢查與恢復保留條件維持不變。

舊版安裝位置的完整私人記錄先驗證 ACL、結構、home 與 session 綁定。所有程序已退出的記錄不再阻擋更新；仍有活程序時一律視為 unknown／uncontrolled，不連線舊管線，也不能用來排除程序阻擋。新版按開啟受管桌面時若遇到舊控制，先持久撤銷授權，再進入可取消的首次啟用等待。等待保留所有記錄內仍活著的 Host／App／backend，通過兩次空快照後才重新啟用。

新增合成驗證涵蓋舊版記錄完整退出、舊 Host 仍活、Host 先退出但 actor 仍活、不同 home 拒絕、禁止舊管線存取，以及原生 WS＋合成 App／backend 的非零退出、意外退出、受管子程序仍活與 Handle 不可用。未關閉使用者 App、未修改真實帳號，真實跨帳號切換及續聊仍未驗證。

最終完整回歸 C# 212／212、companion 合成測試 9／9 通過。另以已安裝桌面套件的實際解析入口與獨立空目錄驗證升級流程：偵測舊控制、持久撤銷、進入排隊、取消成功、代表舊程序的測試程序繼續執行、未建立新 session 或 auth，七項檢查全通過。該驗證只撤銷測試目錄的授權，不修改使用者整合設定或關閉使用者 App；完整的實際 Quit 後更新接續尚待使用者操作確認。

## v1.4.1：首次啟用等待與程序診斷

v1.4.0 的首次啟用只檢查一次程序；遇到既有 App 就立即返回錯誤，所以之後從選單 Quit 不會自動接續。v1.4.1 改成可取消的排隊等待，在建立整合設定、spec 或 Host 之前每 700 毫秒重新檢查，顯示實際類型與 PID；必須空清單、等待一秒、再取得新鮮空清單，才接續啟用。最長五分鐘，晚到的空快照也不能越過期限。Host 內最後一道 first-enable guard 保留，以阻擋最後的重新啟動競態。

候選程序名單與退出要求沒有放寬。下載快取中的桌面 CLI 現可在父程序已退出時仍被正確描述為 Desktop；VS Code／其他 IDE 的父程序與擴充套件路徑優先。來源分類只影響說明，不代表可以自動終止該程序。

本版 C# 203／203、companion 合成測試 9／9 通過。新增九項首次啟用等待回歸，涵蓋 App→剩餘 CLI→兩次空快照、自動接續、取消、程序重現、未知檢查、逾時及慢檢查越過期限；另新增兩項程序分類回歸。實際唯讀驗證使用目前開啟的桌面 App 及隔離空目錄：收到至少兩次等待更新、顯示 PID、沒有 VS Code 提示、取消成功，且沒有建立整合設定、原有程序保持運作。本輪沒有關閉使用者 App；真正 Quit 後的自動接續使用合成測試驗證，原生 Host 的完整閒置退出／恢復實測沿用下列 v1.4.0 範圍。

## v1.4.0：可選桌面受管整合

正式程式新增 `DesktopRuntimeHost.exe`、`DesktopSession.cs`、`DesktopRuntimeCoordinator.cs`、`DesktopNative.cs`、`DesktopWebSocket.cs` 與 `desktop-start.ps1`，接入手動切換／切回上一個帳號及共用的工作協調流程。建置輸出為 `dist-v1.4.0`。實作使用 .NET Framework 4.8、原生 Windows API 與 Windows WebSocket Protocol Component，不要求使用者安裝 Python／Node.js，也不設定 HTTP URLACL。

### 啟用、資料位置與範圍

首次啟用前，使用者須先完成工作，從現有 Codex 選單或系統匣正常 Quit，並正常結束其他 Codex 工作。之後在「實驗整合…」按「啟用並開啟受管桌面」。它不附接既有私人 stdio，也不將另一個查詢程序的空清單當成原 App 閒置。已啟用後可從同一視窗按「開啟受管桌面」；開始功能表另行啟動的實例仍須正常 Quit，不能套用受管實例的閒置證明。

生產啟動保留目前 `CODEX_HOME`、原有 Electron profile 與 SQLite 位置。若指定 `CODEX_ELECTRON_USER_DATA_PATH` 或 `CODEX_SQLITE_HOME`，須為絕對路徑；一般使用者資料不會被研究用空 profile 取代。整合只管理可解析套件身分與隨附 CLI 的 Windows 桌面套件。既有外部 `CODEX_APP_SERVER_WS_URL`、`CODEX_APP_SERVER_FORCE_CLI=1`，或指向套件外 CLI 的 `CODEX_CLI_PATH` 會使啟用被拒絕，不會直接覆寫外部後端設定。

設定與 session metadata 放在切換器本機資料目錄的 `integrations/desktop`，限制為目前 Windows 使用者存取，並檢查擁有者、ACL、路徑與 reparse point。控制管線核對 controller／host 的 PID、建立時間、執行檔與 Windows session；loopback WebSocket 另有隨機憑證、Origin 拒絕、TCP peer 身分與原生祖先鏈驗證。狀態檔只保存執行狀態與程序身分，不保存聊天內容；靜態 catalog 本身不能授權登入寫入，套用前還需即時控制查詢。

### 閒置、兩階段退出與恢復

1. 共用協調器先確認所有相關受管來源可查詢、控制仍有效，且沒有未受管程序。手動切換固定等待閒置，不繼承自動監看的 VS Code 回合中止選項；桌面整合一律不發送 `turn/interrupt`。
2. Desktop Host 在同一 admission lock 下凍結新連線、後端建立與新工作，捕捉 session generation 及所有後端成員。轉送中的要求同步計入待完成工作；同一後端仍可接收既有伺服器要求的回覆，讓已開始的工作正常收尾。
3. 對所有成員先 `prepare`，查詢已載入對話、待處理要求、轉送與背景工作。每個成員都須 known idle；任何成員忙碌、未知、撤銷授權或 generation 變動，皆不得提交 EOF。
4. 提交前再查一次，核對原準備序號、成員清單與授權，才關閉自己持有的 stdin，等待每個 backend 自然以 exit code 0 結束。WS 斷線本身不等於閒置，也不會直接觸發 EOF；舊連線的後端尚未安全退休時，不建立取代它的新後端。
5. 再核對受管 App 的 PID、原生建立時間、執行檔、使用者與 session，只向該身分註冊 Restart Manager，使用 `RmShutdown(flags=0)` 要求非強制 end-session。必須確認 App exit code 0、所有受管後端退出及相關程序安靜，才允許後續登入套用。沒有可用主視窗、App 拒絕或退出逾時時，停止流程，不改為 force／kill／restart。
6. 成功或需要恢復時，使用同一資料位置重新啟動受管 App，重新驗證連線與後端 generation。Host 先回覆恢復要求已接受，Client 再以新鮮記錄輪詢 status，最多等待 120 秒；已接受不等於已就緒，只有 known、running 且授權有效時才移除恢復記錄。輪詢不重送 restore，逾時或停用仍保留記錄。VS Code 由既有 companion 要求對應視窗重載。「恢復已暫停的桌面／VS Code」可重試受管恢復。恢復只重新建立服務，**不送出聊天訊息，不宣稱原聊天畫面或跨帳號續聊已恢復**。

忙碌、unknown、控制撤銷、無法驗證的身分或其他 Codex 程序都會阻擋登入套用。EOF 前可安全撤回的準備會解除凍結；若已部分提交 EOF、App 尚未退出或恢復無法確認，保留需要恢復的狀態，提示正常 Quit／恢復，不把部分退出回報為成功。停用桌面控制會先持久撤銷授權，現有一般轉送可繼續；停用後不再自動關閉或重新開啟 App。

Restart Manager 的 end-session 路徑及 exit code 0 **不能證明 Electron 的所有 App Quit／flush 行為完整執行**。先確認後端閒置並讓其正常退出，與正常中止一個執行中回合，是不同保證。本功能依賴該桌面版本的內部受管啟動路線，仍為可選實驗整合。

### v1.4.0 最終驗證

| 範圍 | 結果 |
| --- | --- |
| 本版 C# 完整回歸與 companion 測試總數 | C# 192／192、companion 合成測試 9／9 通過；Windows PowerShell 5 與內建 C#5 編譯器建置成功。 |
| 原生 WS、身分、兩階段屏障、忙碌／未知／撤銷／取消／重連回歸 | 原生 WS 18 組、Session 36 組均包含於上述完整回歸；包含實際 native WS＋合成 App/backend、early ACK 與就緒分離、逾時保留記錄及停用撤銷。忙碌回合的測試使用合成後端。 |
| 本版原生 Host 的真正 App 啟動、閒置退出與恢復 QA | 桌面套件 26.1021.3705.0、隨附 CLI 0.164.0-alpha.11.1：10 項檢查全通過，兩代後端均 EOF／exit0，兩次桌面 RM flags0／exit0，恢復後再次 known idle，最後 App、後端及測試 Host 全部退出；15 個原有相關程序均保持運作。 |
| 真實登入套用、恢復後帳號核對、原聊天與跨帳號續聊 | 尚未驗證；不自動送出訊息。 |

本次真實 App QA 使用獨立編譯的測試入口、全新空白 `CODEX_HOME`／SQLite／Electron profile，以及僅回覆拒絕的本機 proxy，未建立 `auth.json`、未呼叫模型。為保留正在使用的桌面 App，測試入口只略過首次啟動的既有程序阻擋，退出範圍檢查則排除測試前已存在的 App；正式入口保留完整阻擋。另加入僅記錄正常退出結果的測試診斷。八個共用來源檔經比對，在上述兩個 Host 測試差異還原後與交付版本一致；沒有用早期 Python 原型代替本次原生 Host 驗證。

實測曾找到並修正三項問題：主程序退出後須等待 Electron 子程序真正退出；已提交 EOF 的恢復狀態不能隨 Host 提早退出而遺失；Windows WebSocket 自動回覆 PING 後不能留下假的不完整訊息期限。最後一項先在舊版重現相同例外，再以 raw PING 後閒置 31.5 秒仍可收訊的測試驗證修正，同時保留半截 frame 及 fragmented message 的 30 秒限制。

程序狀態查詢使用原生 session ID，並只對新出現的 App 身分重查祖先；PID 重用或未知新程序仍須重新驗證。最後一次真實 App QA 的 status 要求耗時 18～557 毫秒，恢復接受回覆約 480 毫秒。這是該次測試量測，並非所有電腦的效能保證。

另以交付版 `DesktopRuntimeHost.exe --launch` 經正式啟動腳本與 Windows 套件入口驗證首次啟用保護：既有 App 保持運作，Host 拒絕啟動另一個受管 App，沒有建立新登入。此測試發現並修正繼承 PowerShell 7 的 `PSModulePath` 時，Windows PowerShell 5.1 自動載入不相容模組的問題；腳本明確載入 Windows 內建模組，保留原環境設定。啟動器另以合成執行檔驗證既有發行檔直接啟動，以及缺少桌面元件時自動建置，兩項均通過。

## v1.3.1 手動閒置切換（歷史版本）

手動選取帳號切換與切回上一個帳號，固定呼叫不允許中止回合的等待入口。已整合 VS Code 的回合、待處理要求與背景 terminal 全部結束後，正常關閉該後端的 stdin、等待 exit code 0，再經安靜期與寫入前程序檢查才套用帳號。自動監看的正常中止選項不影響手動操作。

新增三組真實 Windows Named Pipe＋合成後端流程測試，確認閒置正常退出後才允許套用；進行中回合、待處理要求、背景工作、未知狀態、未受管程序及停用控制時只查詢、不中止或退出；退出前新工作到達而被拒絕時不套用，恢復也不重載仍可工作的後端。回歸測試 127／127 通過，companion 9／9 通過。這一輪沒有操作真實 Codex 對話或帳號。

v1.3.1 當時的桌面 App 與未整合程序仍保持阻擋；介面會列出來源、PID 與正常退出方式，不能把狀態未知當成閒置。v1.4.0 新增上方可選受管路線，未受管實例仍保持此限制。

## v1.3.0 的能力與邊界（歷史版本）

自動監看預設關閉，啟用後每 60 秒檢查目前帳號。同一時間只有一輪查詢／切換；只有目前帳號已確認耗盡，才按手動順位逐一查詢其他帳號。成功回覆只有每週上限且仍有剩餘時也可列入候選；查詢失敗或資料未知不會當成可用。不會因較高順位帳號恢復額度就切回。

選定候選者後，先協調相關工作退出，再重查目前帳號仍耗盡、候選帳號仍可用、目前登入及順位未變更，最後確認程序安靜才寫入。隔離的額度查詢後端須完成清理，其空工作清單不能用來代表使用者正在操作的 App 或 IDE 閒置。

可選的 VS Code bridge 會在使用者明確啟用並首次重新載入後，管理該擴充套件新啟動的實際 stdio 後端。它不會附接已經啟動的私人 stdio 連線。手動切換固定等待工作閒置；自動監看若要正常中止回合，須在切換器另外勾選。工具不會強殺 `Code.exe`、共用擴充套件宿主或外部程序。

**工作狀態未知不得視為閒置。** v1.3.0 的未整合視窗、無法驗證的程序與 Windows 桌面 App 都會阻擋切換；當時桌面 App 須使用者完成工作並正常 Quit，切換完成後再開啟。v1.4.0 可以管理另行啟用的桌面實例，但仍不會自動中止桌面 App 的現有工作。

VS Code 恢復採用對應視窗的 `workbench.action.reloadWindow`。它不會自動送出聊天訊息或接續對話。v1.3.0 提供的「重送已暫停 VS Code 的恢復要求」，在 v1.4.0 改為「恢復已暫停的桌面／VS Code」；VS Code 仍只接受相同登入快取、已驗證管線且確定閒置暫停的 runtime。**已在隔離的真實 VS Code 視窗驗證合成回合中止、後端正常退出、companion 重載及新後端恢復閒置。** 原對話是否恢復尚未確認，真實登入套用與跨帳號續聊尚未驗證，因此仍是可選的實驗整合。

## 測試版本與環境

| 檢查範圍 | 版本 |
| --- | --- |
| 桌面 App 隨附 CLI 的隔離協定實驗 | 0.163.0-alpha.11.2 |
| VS Code 隨附 CLI 的隔離協定實驗 | 0.162.0-alpha.17.2 |
| VS Code 擴充套件靜態檢查 | 26.1007.21434 |
| 更新後的 Windows 桌面套件靜態檢查 | 26.1021.3705.0 |
| 更新後桌面 App 的 CLI 版本／程序與 listener 觀察 | 0.164.0-alpha.11.1 |
| 本輪中止後續聊與重送驗證 | 0.162.0-alpha.17.2、0.164.0-alpha.11.1 |
| 本輪桌面 Quit／導航函數提取 mock | Windows 桌面套件 26.1021.3705.0 |
| 本輪實際 VS Code 視窗 | VS Code 1.143.1、官方 Codex 擴充套件 26.1007.21434 |

前兩版「隔離協定實驗」使用自行啟動的子程序、獨立的空 `CODEX_HOME`、合成對話與僅監聽 loopback 的模擬 Responses SSE 服務。未讀取真實登入或對話，未關閉現有 Codex App、VS Code 或 CLI。模擬模型請求沒有 Authorization header，測試目錄未建立 `auth.json`；該輪測試未觀測到被代理攔截的外網請求。沒有做全系統封包擷取，不將代理觀察擴張為整台電腦沒有其他網路活動。本輪續聊實驗與實際視窗驗證的網路觀察另列於各節。

前兩個實驗版本的 JSON Schema 均成功產生，包含中止、恢復與背景 terminal 查詢介面；未找到通用的 app-server shutdown RPC。本輪另外對更新後的桌面 CLI 執行了中止後續聊與重送實驗，但沒有重跑前兩版的全部 WebSocket／傳輸實驗。

## 已通過的隔離協定實驗

以下結果在兩個實驗版本皆通過。回合只包含合成模型輸出，背景 terminal 查詢結果為空；這一組實驗沒有測試中止時仍有工具或背景命令執行的情況。

| 實驗 | 觀察結果 |
| --- | --- |
| 正常中止進行中的回合 | 在模擬模型已開始串流後送 `turn/interrupt`，收到成功回覆與 `turn/completed`，狀態為 `interrupted`。 |
| 自行持有的 stdio 後端正常退出 | 中止完成後關閉該子程序的 stdin，程序自然結束，exit code 為 0。 |
| 重啟並恢復對話 | 新程序使用相同測試資料夾，以 `thread/resume` 恢復合成對話；下一個合成回合完成，再正常退出。這是協定實驗，產品不會自動發送該下一回合。 |
| 獨立 stdio 後端無法控制原回合 | 另一個使用相同測試資料夾的程序看不到原程序的執行中對話；中止請求收到 `thread not found`。 |
| 同一 WebSocket listener 的第二個控制連線 | 第二個用戶端可看到同一執行中對話並正常中止，原用戶端收到 `interrupted`。 |

WebSocket listener 在用戶端及 stdin 關閉後沒有自行退出。實驗最後僅清理自行持有的測試程序，**未證實 WebSocket 後端的正常退出方法**；此清理方式不是產品對外部程序的退出策略。

自建 Unix socket 測試因目錄權限檢查被拒絕，未完成 proxy 連線驗證。這不代表 Windows 不支援該傳輸方式。

## 本輪中止後續聊與重送實驗

使用真正的 VS Code CLI `0.162.0-alpha.17.2` 及桌面 CLI `0.164.0-alpha.11.1`，兩版結果一致。模型回覆由 loopback SSE 合成；這一節不使用實際 App／VS Code 視窗，也不包含真實登入、工具呼叫或跨帳號切換。

| 步驟 | 兩版共同結果 |
| --- | --- |
| 中止與退出 | 模型開始串流後送 `turn/interrupt`，收到 `turn/completed: interrupted`；確認背景 terminal 為零後送 stdin EOF，後端自然以 exit code 0 結束。 |
| 重啟後 `thread/resume` | 保持原 thread ID，原回合仍是 `interrupted`。等待一秒並查詢後仍 idle，模型請求沒有增加。 |
| 對已中止回合 `turn/steer` | 收到 `no active turn to steer`，不會重新啟動原回合。 |
| 送一次新的 `turn/start` | 產生新的 turn ID，合成回合完成，模型請求增加一次。這是新回合，不是從原 token／工具指令中途續跑。 |
| 相同 `clientUserMessageId` 重送 | 同程序內及重啟後再次發送相同參數，都產生另一個 turn 並再次呼叫模型，沒有去重。 |

每版共啟動三個後端，全部自然退出；各有四次合成模型請求：初始回合、首次續聊、同程序重送、重啟後重送。模型請求未觀察到 Authorization header，測試 home 沒有 `auth.json`；該輪阻擋代理未收到外部 CONNECT／非模型 POST。這只描述測試程序與該代理的觀察。

因此 `thread/resume` 不能被回報為「已自動續聊」。`clientUserMessageId` 可用於關聯訊息，但此兩版實測顯示它不能用作重試去重鍵；JSON-RPC request ID 也只是對應連線上的回覆。合成測試沒有工具副作用，無法保證真實檔案修改、外部 API 或背景命令不會被重做；「不要重做」提示不是去重保證。

產品的 VS Code 恢復只要求重載視窗並重新建立後端連線；v1.4.0 的桌面恢復則重新啟動受管 App。兩者都不自動送出下一則訊息。未來若提供可選自動續聊，至少需要只處理本次切換工具自己中止且已收到 `interrupted` 的回合，保存不含 prompt／token／工具輸出的本機恢復紀錄，並核對目標帳號、thread、最後 turn、工作區與 idle 狀態。先原子記錄 `dispatched` 再送一次固定續聊訊息；若程序崩潰或回覆遺失而無法確認結果，必須改為需要使用者確認，不能直接重送。這只能提供保守的「至多一次送出嘗試」，不保證一定續跑；同時保證送達與不重複副作用，仍需要服務端持久去重或有副作用的工具各自去重。這些是設計限制，尚未開放產品自動續聊功能。

## VS Code bridge 與 companion 驗證

6／6 組整合測試使用**真實 Windows Named Pipe、bridge 與自行啟動的模擬後端**，沒有使用真實 VS Code 視窗或模型服務：

1. 轉送原始用戶端 request ID 與伺服器回呼，控制器內部回覆不混入編輯器連線，catalog 不保存對話內容。
2. 同一受管後端的進行中回合正常中止，之後透過 stdin EOF 正常退出；恢復僅設定重新載入要求，暫停時拒絕新工作。
3. 分頁查詢所有已載入對話與背景 terminal，有背景工作時不允許退出。
4. 未知查詢、過期 generation 與重複 process handle 不得導致誤判閒置。
5. 等待退出逾時或後端非零退出不得回報已安全暫停。
6. 停用授權後立即拒絕中止／暫停，但一般編輯器請求仍可轉送。

另有 9／9 組獨立 Node VM 模擬測試，將 VS Code、檔案、管線與 helper 呼叫全部替換成測試物件，涵蓋：

- 原設定為未設定、`null` 或自訂路徑時，重複啟用再停用仍精確還原。
- 取消啟用、Remote 視窗與不符合擁有者 PID 的 catalog 不會觸發控制或重載。
- 等待驗證期間停用整合或釋放擴充套件，不會隨後誤觸重載。
- 有效的恢復要求只重載一次。

先前 VSIX 已使用獨立的 VS Code 使用者資料與擴充套件目錄驗證安裝，並確認套件與入口檔存在；當時只驗證安裝。本輪額外完成的實際視窗測試另列於下節，不將合成模型測試擴張為真實登入驗證。

實作與審查另確認：轉送中的訊息納入待完成工作、凍結新請求後再確認閒置、寫入與退出等待有期限、真正送出中止與提交 EOF 前再次檢查授權、EOF 前失敗可解除凍結、僅持有自己的後端、不以強殺作為逾時處理。管線和本機設定限目前 Windows 使用者存取；控制連線會核對程序、建立時間與 generation。這些程式碼檢查不等於每個競態都已在實際編輯器中重現測試。

2026-10-24 的 C# 回歸測試 **124／124 通過**，companion Node 模擬測試 **9／9 通過**。新增回歸涵蓋：`quiesce` 因忙碌而拒絕、backend 仍為 running 時，`RestoreAsync` 不應發出多餘重載，避免原本仍可工作的視窗被重新載入。取消後控制管線延遲釋放的情況也有回歸測試；恢復時會在期限內等待狀態查詢連線，已送出的要求不會重送。此 v1.3.0 修正當時沒有開放自動 Desktop Quit 或自動續聊。

## 本輪實際 VS Code 視窗驗證

使用 VS Code `1.143.1`、官方 Codex 擴充套件 `26.1007.21434` 與其真正隨附 backend，搭配實際 bridge 和 companion，在隔離使用者資料／擴充套件目錄及合成工作區執行。測試預先建立 companion 已啟用狀態，並把設定導向私人測試資料夾；bridge 控制邏輯未變，沒有修改一般使用者的正式設定。這驗證已配置整合後的生命週期，不包含首次點選啟用與授權流程。

| 實際視窗步驟 | 結果 |
| --- | --- |
| 官方擴充套件初始化與受管後端識別 | 通過；最初查詢為 known idle。 |
| 從實際 Codex webview 啟動合成回合 | 通過；官方擴充套件及真正 backend 處理請求，模型回覆由本機 SSE 合成。 |
| 忙碌且不允許中止時要求暫停 | 正確拒絕，未把執行中回合當成閒置。 |
| 正常中止與 backend 退出 | 收到正常中止完成，再以 stdin EOF 讓受管 backend 正常退出。 |
| companion 要求對應視窗重載 | 通過；產生新的 extension host、backend 與 generation，重新查詢為 known idle。 |
| 編輯器檔案與分頁 | 測試檔案保留，編輯器分頁恢復。這不保證所有擴充套件狀態或未送出草稿都能保留。 |
| Codex 側欄與原對話 | 重載後能開啟側欄；其後 20 秒未觀察到指定 thread 的 resume 記錄，尚未確認原對話恢復。這不能證明畫面一定沒有恢復。 |
| 真實帳號切換與跨帳號續聊 | 未測試，不能以新 backend 閒置代替登入成功或原聊天已續跑。 |

本輪記錄到兩次本機合成模型請求，未觀察到 Authorization header，測試 home 未建立 `auth.json`。loopback 阻擋代理另記錄 **37 次外網連線企圖並阻擋**，不能描述為沒有外網企圖，也不能沿用獨立 CLI 協定實驗的零攔截結果。這是測試代理的觀察，不是全系統網路稽核。

此結果證明這組版本在已配置的隔離視窗中，可以正常中止合成回合、退出受管 backend 並重載恢復運作。原對話是否恢復仍未確認，也沒有驗證真實登入套用、工具副作用或跨帳號續聊。

## VS Code 的版本相依性

本機擴充套件的 `package.json` 與 `out/extension.js` 顯示：

- 內部 `interruptTurn` 使用 `turn/interrupt`，但公開命令清單沒有 stop、interrupt 或 restart，啟用函式也沒有外部控制 API。
- 擴充套件自行啟動 app-server；後端結束會清理連線及待處理請求，非零退出會通知錯誤。
- 內部 `codex-app-server-restart` 事件呼叫 `workbench.action.reloadWindow`，未看到後端退出後自動就地重連的流程。
- `chatgpt.cliExecutable` 標示為開發用途且具有 application scope，其他視窗與設定檔下次啟動也可能受影響。

因此本工具透過使用者明確啟用的 executable override 取得新後端的 stdin／stdout，再由 companion 在確認目前視窗的擁有者與存活 generation 後要求重載；其他未整合視窗仍須自行正常退出。只停止 Codex 後端通常不會關閉 VS Code 編輯器，但會讓 Codex 功能斷線；重載會影響整個對應視窗的擴充套件狀態與未送出的草稿。

停用整合會撤銷中止／暫停授權並還原原設定；已在進行的一般 stdio 轉送不會被強制關閉。對已暫停後端保留的恢復標記不會使已停用的 companion 自動重載，使用者須手動重新載入。安裝與命令步驟見 [使用說明](../README.md#啟用-vs-code-實驗整合)。

## 更新後的 Windows 桌面 App 觀察（v1.3.1 時期研究）

對 26.1021.3705.0 套件與 CLI 0.164.0-alpha.11.1 的唯讀檢查，使用父 PID、執行檔與建立時間確認桌面 App 直接擁有的 CLI；觀察時桌面程序及其直接 CLI 子程序沒有 TCP listener，預設 control socket 也不存在。這不能排除任意自訂位置，但沒有找到可驗證的現有 runtime 控制連線。

安裝程式碼明確將 Windows 排除於可選 local-daemon 分支之外，預設使用私人 stdio。內部 `CODEX_APP_SERVER_WS_URL` 可指定 WebSocket，但一般 local host 路徑沒有找到可設定驗證 header 的握手入口；token 握手出現在 durable/cloud 分支。單純啟動一個 loopback listener 不能替代受驗證的控制通道。

Microsoft Store／MSIX 的 `ActivateApplication` 能保留套件身分，但沒有環境區塊參數，不能假定呼叫端的環境會傳入 broker 啟動的 App。套件也沒有主程式的 execution alias。`IPackageDebugSettings::EnableDebugging` 雖有環境參數，卻會改變套件的暫停、終止、恢復及啟動逾時行為，因此未採用。未找到公開的 local host 持久設定可安全指定受管啟動程式。

此版本有正常 Quit 入口：Windows `Ctrl+Q`、App Quit 選單及系統匣 Quit。`bootstrap.js` 的 `window-all-closed` 處理在 Windows 不會呼叫 `app.quit()`，因此關閉所有視窗仍不能證明 App 或 backend 已退出。

正常 App Quit 與正常中止回合的保證不同。`main.js` 的 `Dvt`／`before-quit` 在較早的 modal guard 之後，Windows 分支會略過 active-chat 確認。`will-quit` 先 flush，再 dispose context 並再次 quit；只有 context dispose 的等候上限是 5 秒，前段 flush 不能一併宣稱有 5 秒整體上限。context 清理連到 connection `dispose` → `stopProcess` → stdio transport `close`，後者對仍在執行的 child 直接呼叫 `kill()`。觀察到的退出鏈沒有先對所有 active turns 發出 `turn/interrupt` 並等待 `turn/completed`，不能把正常 Quit 當成對話已正常收尾的證明。

內部 `quit-app` 訊息受 `isTrustedIpcEvent` 檢查；內部存在 `interruptTurn` 也不代表外部切換器取得既有私有 transport 的控制權。沒有找到可供本工具使用且已驗證的公開外部 Quit／interrupt 端點。`Qvt` 的原聊天 deep-link 導航將 prompt 放入 `prefillPrompt`，不會送 `turn/start`；開啟原聊天或帶入 prompt 不能回報為自動續聊。

本輪從安裝碼抽出 Quit、視窗關閉、stdio close 與聊天導航函數，使用 Node VM 的假 app／dialog／child／navigation 物件驗證，**9／9 通過**。涵蓋 Windows 略過忙碌確認、較早 modal guard、非 Windows 取消、Quit 清理與重入、前段 flush 的 timeout 邊界、Windows 關閉視窗仍存活、Linux 對照、child kill，以及 prompt prefill。沒有載入 Electron、沒有退出目前 Desktop App，也沒有實測 Desktop 重新啟動或自動續聊。

為使版本相依的判斷可核對，以下為研究副本的 SHA-256；函數名稱只對應這一版壓縮安裝碼，並非公開 API。

| 來源／識別標記 | SHA-256 |
| --- | --- |
| `main.js`：`Dvt`、`Tvt`、`Qvt` | `2327d031e32c8d80c0dbf9bd81144466a90f8e49d3bc64e6604c23fbab900d7f` |
| `bootstrap.js`：`$P`／`window-all-closed` | `3081dd9f7fdf0eba02d8caf99becb6f891e7bd9a3b1f7345537eb10165e16ff1` |
| `network.js`：stdio `close`／`spawnProcess` | `9775ead462eb9ab9c31b4a8aedf92b18a5a657f186be21f207a9b7adb143895f` |
| `shared-node.js`：connection `dispose`／`stopProcess` | `a42052ee3309565865f2d20fc45dee297935cbb50d1fc1a404b90c18d824b93c` |

以上靜態分析仍不足以安全接管現有桌面 App，故 v1.3.1 對桌面工作狀態保持未知，要求使用者先完成或自行停止工作，再正常 Quit。v1.4.0 以另行啟用的受管實例取得實際後端狀態，沒有以本節的 Quit 入口分析取代閒置證明，也沒有開放自動桌面回合中止或續聊。

## 2026-10-25 桌面狀態來源再驗證

目前安裝套件仍為 `26.1021.3705.0`，與研究副本一致。找到較完整的內部 `getWorkInProgress()` capability：它可列出目前 host 的回合、待開始／恢復的工作、待送出與未確認的訊息、要求、排隊續聊及目標。取得 current manager 時會核對 capability 的 generation，但這份清單本身沒有附上完整性、新鮮度或程序身分證明，也沒有單獨查詢所有背景 terminal。

該 capability 經過可信任 renderer 的 MessagePort 匯出；一般跨 client 的 IPC 並未提供這項呼叫。一般 IPC 的新連線只收到自己的 client ID，不會取得所有既有 client／對話或重播先前的工作事件；`following-status` 只代表對話串流的訂閱狀態。App 工具管線亦有 ready view 與 `canCallTool` 能力檢查，不能把模型工具的已授權環境當成外部程式的控制 API。

SQLite／本機 catalog 會丟棄 runtime 狀態並排除部分子對話；cached metadata 還會把 active 狀態轉為 `notLoaded`。因此掃描歷史、沒收到 IPC 事件、查不到活躍資料或 CPU 低，都不能據以關閉桌面 App。

抽取現有函數的 21 組狀態／儲存測試，加上 5 組 IPC router／匯出邊界測試均通過。這些均使用合成物件，沒有連線至現有 App、查閱真實對話或帳號，也不代表桌面控制已完成。這項研究沒有取得可附接任意現有 App、且能完整證明閒置的外部通道；v1.4.0 因此改用明確受管啟動，查詢同一 runtime 的工作並在退出前阻擋新工作與再次檢查。

## 桌面受管啟動與正常退出候選（早期研究）

找到另一條不需要附接現有私人 stdio 的實驗方向：Windows 的 `Invoke-CommandInDesktopPackage` 可讓自製啟動器與子程序保留已安裝套件身分，再以程序限定的環境設定啟動 App。自製啟動器與子程序的套件身分和環境值繼承已實測通過。該命令屬 Microsoft 診斷機制，其 token 不等同一般啟動；這仍是實驗候選，不能直接視為正式支援的啟動 API。

安裝碼另確認，有效的 `CODEX_APP_SERVER_WS_URL` 且未強制使用 CLI 時，會停用一般的本機 client coordination。這使私有、具隨機驗證憑證的 loopback WebSocket 代理可以連接自行持有的實際後端，同時隔離固定的 Windows `codex-ipc`。單靠私有 `CODEX_HOME` 或視窗資料目錄無法隔離該管線。啟動設定相關函數的 12 組合成測試已通過；代理必須拒絕未授權握手，不能只靠 loopback 限制。

Windows Restart Manager 的 `RmShutdown(flags=0)` 已以自製 WinForms 主視窗實測：允許時正常退出；拒絕時保持存活；只有指定 PID／建立時間被註冊，共用檔案的旁觀程序仍存活。窄工具還會核對執行檔、使用者及 Windows session，且不使用強制退出或系統重啟。純背景或沒有可用主視窗的程序，在這個實驗中無法由此退出。這是 end-session 退出流程，不等於已驗證 Electron 的 App Quit／完整 flush。

桌面 App 會因啟用功能而重連。整合必須以穩定的桌面 session 管理各連線與後端 generation；先全域拒絕新工作，再檢查並正常退出所有相關後端，最後關閉 App。現有 VS Code bridge 以單一 stdio client 為前提，不能把任意 WS 斷線都直接當成可安全退出。

當時正式元件的候選為 .NET Framework 的 `TcpListener` 搭配 Windows 內建 WebSocket Protocol Component，只先驗證原生 server handle。v1.4.0 已據此實作 HTTP 驗證、transport adapter、framing 與取消處理；最終回歸與真正 App QA 另見本文開頭，不能把早期 handle probe 當成完整驗證。

## 2026-10-25 真實桌面閒置退出實驗：早期原型通過

以目前安裝的 Windows 桌面套件 `26.1021.3705.0`，使用新的私有 ACL 資料夾、空登入快取、獨立 SQLite／Electron profile，以及具隨機驗證憑證的 loopback WS 代理，完成一次真正 App 的完整閒置退出流程。App 程式與正式設定沒有修改，沒有 UI 自動化，也沒有真實帳號切換、模型回合或工具執行。

| 實際步驟 | 觀察結果 |
| --- | --- |
| 保留套件身分啟動 App | 新 App 連入自行持有的後端；TCP peer PID、建立時間與原生執行檔路徑符合該次啟動。 |
| 代理驗證 | 未授權握手收到 HTTP 401。 |
| 同一後端初始化與登入狀態 | App 的 `initialize` 成功；`account/read` 回覆 account 為 null。此 App 不需要送出 `initialized` 通知。 |
| 真實工作查詢 | 七次穩定 Known Idle，進行中回合、待處理要求及背景 terminal 均為零。 |
| 全域停止接收新工作 | 在共享 admission lock 下凍結連線與後端建立；新握手收到 HTTP 503，凍結後沒有新增後端。既有 App 訊息也不再轉送。 |
| 後端退出 | Controller 回報 `quiesced`、known、idle；stdin EOF 後 backend 與 bridge 皆以 exit code 0 結束。 |
| 桌面程式退出 | 再次核對 App 身分，僅註冊該 PID／建立時間；`RmShutdown(flags=0)` 回傳成功，App 以 exit code 0 結束。沒有 force 或 restart。 |
| 最終清理 | 研究 App、backend、bridge、wrapper 與代理均已退出，沒有新增 App／backend 殘留；原先記錄的 19 個程序身分全部仍存活。 |

本輪沒有建立 `auth.json`，沒有模型請求；拒絕代理記錄到 24 次其他 HTTP(S) 企圖。這只描述該代理的觀察，不是全系統網路稽核。三個不在啟動白名單內的要求被拒絕，包含讀檔及外部設定探索，沒有據此讀取真實資料。

本次真正跑過的是單一 backend generation。多代連線 ledger、父程序建立時間／存活區間與凍結競態已做來源審查，但本次成功流程沒有觸發多代重連；不能把它回報為多代恢復測試通過。既有 App 僅核對程序身分與存活，沒有檢查其對話內容或內部狀態。

此早期原型證明：桌面 App 可經受管啟動，在確認同一後端閒置並凍結新工作後，自動退出。它當時未接入 v1.3.1；v1.4.0 的原生 Host 與手動按鈕是另行實作，須採用本文開頭獨立的最終驗證結果。RM 的 end-session 及 exit code 0 不代表 Electron 的所有 Quit／flush 行為均已驗證；真實登入套用、帳號核對、原聊天恢復、忙碌工作、多代重連與失敗恢復，仍須分別驗收。首次啟用須先正常退出原有 App，再以受管模式啟動，不能直接接管現有私人 stdio。

## 尚待完成的實際視窗驗收

1. 實際視窗首次啟用／授權流程，以及多視窗、不同設定檔與其他擴充套件並行時的影響。
2. 真實模型回合中的核准等待、工具與背景 terminal，以及新工作與退出競態；未知狀態必須阻擋切換。
3. 真實帳號的候選重查、登入套用與恢復後帳號核對；原聊天畫面自動恢復、跨帳號歷史可見性與續聊。
4. 在實際視窗中驗證取消、逾時、停用整合、版本更新與恢復失敗；既有模擬測試不能取代這些操作驗收，不自動升級為強制終止。
5. 桌面 App 的原生 Host、兩階段屏障與正式按鈕已在 v1.4.0 實作；本版完整回歸與真正 App 的兩代閒置退出／恢復結果見本文開頭。實際登入後的忙碌／核准／背景工作、異常重連及未來版本更新仍須另行驗收，不能由早期單代閒置原型代替。

本次沒有驗證真實跨帳號續話。合成對話中的 `thread/resume` 成功，不能保證跨帳號歷史可見性、登入狀態或操作可安全重送；產品不自動重送訊息。

## 參考

- [OpenAI App Server：中止、對話狀態、傳輸與恢復介面](https://learn.chatgpt.com/docs/app-server)
- [Codex 桌面 App 指令：Quit 與關閉視窗的區別](https://learn.chatgpt.com/docs/app/commands)
- [Codex IDE 公開命令](https://learn.chatgpt.com/docs/ide/commands)
- [Microsoft ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication)
- [Microsoft EnableDebugging 的環境參數與生命週期影響](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-ipackagedebugsettings-enabledebugging)

- [Microsoft：套件身分下的診斷啟動](https://learn.microsoft.com/en-us/powershell/module/appx/invoke-commandindesktoppackage?view=windowsserver2025-ps)
- [Microsoft：Restart Manager 的正常關閉要求](https://learn.microsoft.com/en-us/windows/win32/api/restartmanager/nf-restartmanager-rmshutdown)
- [Microsoft：Windows WebSocket Protocol Component](https://learn.microsoft.com/en-us/windows/win32/websock/web-socket-protocol-component-api-portal)
- [OpenAI：Hooks 的事件與阻擋範圍](https://learn.chatgpt.com/docs/hooks)
