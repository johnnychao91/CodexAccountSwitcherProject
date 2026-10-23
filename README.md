# Codex 帳號切換器

Windows 原生、繁體中文的本機帳號管理小工具。使用 Windows 10 / 11 x64 與 .NET Framework 4.8，無須安裝 Python、Node.js 或額外套件。從 GitHub 取得原始碼後，雙擊根目錄的「Codex帳號切換器.cmd」即可開啟；若缺少執行檔或設定檔，啟動器會先自動建置。也可手動執行 `powershell -ExecutionPolicy Bypass -File .\build.ps1`，再開啟 `dist-v1.0.2\CodexAccountSwitcher.exe`。建置產物留在本機，已由 `.gitignore` 排除。

## v1.0.2 更新

修正「結束 ChatGPT 後持續等待，手動開啟仍是原帳號」時提示不明確的問題。若 VS Code 的 Codex 擴充功能仍在執行，切換器會等待它退出，尚未進入帳號套用步驟。新版會區分並列出桌面 App、VS Code、其他 IDE、CLI 或未知程序，以及 PID 和應關閉的來源。

等待中的帳號尚未套用，請等工具完成，不要先手動重新開啟 Codex。等待最多 5 分鐘，逾時或取消都不會進入寫入步驟。所有相關程序退出後需通過一秒安靜期與再次檢查，才會保存、套用、讀回核對並啟動。工具不會強制關閉 VS Code 或終止你的工作。

更新時，先取消舊版等待並關閉舊版切換器，再開啟 `dist-v1.0.2` 的新版；帳號庫沿用。根目錄的 `Codex帳號切換器.cmd` 已指向新版。

## v1.0.1 更新

修正自動開啟桌面 App 出現「處理程序沒有套件識別資料」的問題。Microsoft Store / MSIX 安裝現在會從套件資訊解析正確的 Application ID，交由 Windows `IApplicationActivationManager` 啟動，保留套件身分；啟動失敗時不會再直接執行套件內的 EXE。一般非套件程式仍使用原本的 EXE 啟動方式。

此修正已包含在 v1.0.2。已儲存帳號會沿用，不需重新新增。

## 使用方式

1. 開啟工具，按「儲存目前帳號」收藏正在使用的 ChatGPT 帳號。
2. 按「新增帳號」，在官方瀏覽器登入頁選擇另一個帳號。若瀏覽器自動選到原帳號，在登入頁切換使用者。此步使用獨立的暫存登入目錄，不更動目前 Codex 登入。
3. 選取已儲存的帳號，按「切換並開啟 Codex」。
4. 依工具顯示的來源完成退出：若列出 **VS Code，先儲存工作並關閉所有 VS Code 視窗**，或停止其 Codex 擴充功能直到對應程序退出；再從 ChatGPT / Codex 選單選「結束 / Quit」，也結束仍在執行的 Codex CLI。**只按視窗右上角 X 可能仍留在系統匣。** 程序全部結束後，工具會自動套用帳號並重新開啟。
5. 在 Codex 的帳號選單確認切換結果。需要返回時按「切回上一個」。

「重新命名」可標記個人、工作、研究等用途。「移除」只移除此工具保存的帳號及其復原副本，不會登出目前 Codex，也不會撤銷伺服器上的登入。「取消」可中止等待切換或新增登入。登入逾時為 10 分鐘。

## 支援範圍

- 支援 `CODEX_HOME\auth.json` 的 ChatGPT 登入快取；未設定 `CODEX_HOME` 時使用 `%USERPROFILE%\.codex\auth.json`。
- 使用同一快取的 CLI / IDE 會跟著變更帳號。這不是同時執行多個帳號的隔離環境。
- 會辨識此電腦目前的 OpenAI.Codex 桌面套件，即使程式名稱已變為 ChatGPT.exe。
- 不支援 keyring / auto / ephemeral 儲存、API Key、外部存取權杖與受管理的特殊登入流程。遇到已辨識的不相容設定會停止套用；組織政策仍由 Codex 自身執行，本工具不修改政策。
- 帳號清單使用本機憑證中的顯示資訊，沒有驗證遠端登入有效性。存取權杖到期通常由 Codex 自行更新；已撤銷或失效的登入需用「新增帳號」重登。相同帳號的新登入會顯示「新登入，待套用」。
- 切換只更換登入快取，不隔離聊天、專案或設定；各帳號的雲端資料可見性由 Codex 決定。
- 此工具為獨立實作，沒有修改或注入 Codex 桌面 App，也不是官方多帳號 API。未保證日後版本仍採用相同快取格式。

## 本機資料

帳號、名稱、登入憑證、上一個登入的復原資料，全部以 Windows DPAPI CurrentUser 加密後存於：

`%LOCALAPPDATA%\CodexAccountSwitcher\accounts.dpapi`

資料夾 ACL 限目前 Windows 使用者及 SYSTEM。此帳號庫不能直接搬到另一位 Windows 使用者或電腦使用。同一使用者身分執行的其他程式仍可使用 DPAPI，因此這不取代 Windows 帳戶保護。工具不記錄或顯示權杖、不要求輸入密碼、不把帳號庫上傳。

新增登入時，官方 Codex 會暫時把登入快取寫入上述目錄下的私有 `login-<隨機識別碼>` 目錄；完成、取消或失敗後清除。若工具被強制終止，這些目錄可能殘留；確認沒有登入作業後可刪除 `login-*` 目錄。實際啟用的 `auth.json` 仍是 Codex 原本使用的明文格式，應視為密碼保護，勿提交至 Git 或分享。

切換前，工具先保存最新的目前登入與加密復原副本，再以同一磁碟區上的原子替換套用快取。備份失敗或偵測到 Codex 仍執行時會停止。若無法自動啟動桌面程式，可從開始功能表手動啟動。

## GitHub 發佈與私人資料隔離

原始碼與帳號資料分開存放。帳號庫、登入暫存與 Codex 的使用中憑證位於前述 Windows 使用者資料目錄，不需要複製到專案，也不應加入 Git；加密的 `accounts.dpapi` 仍屬私人資料。

在專案目錄執行以下指令，產生可供上傳的原始碼副本：

```powershell
powershell -ExecutionPolicy Bypass -File .\prepare-github.ps1
```

預設輸出至專案旁的 `codex-account-switcher-github` 目錄。目的地必須尚不存在；再次匯出時，可用 `-Destination ..\codex-account-switcher-github-next` 指定新的空白位置。腳本不會覆寫既有目錄，也不會自動建立 Git 儲存庫或上傳。

匯出採固定檔案清單，只包含原始碼、測試原始碼、建置腳本、啟動器、設定及本說明。截圖、`research/`、編譯結果、測試輸出、舊 ZIP、帳號庫和登入快取都不會帶入。腳本會檢查常見憑證樣式、非範例 email、硬編碼磁碟或網路分享路徑、使用者家目錄、Windows 使用者 SID 與主機名稱，發現問題只回報檔名、行號和類型；複製後會比對檔案雜湊。這是有限樣式檢查，不能取代對新加入內容的人工審查。新增公開檔案時，需一併更新腳本內的檔案清單。

請只將產生的原始碼目錄提交至 GitHub。`.gitignore` 另排除常見帳號檔、登入暫存、憑證、資料庫、日誌、截圖與建置產物；它不會移除已追蹤的檔案或清除既有 Git 歷史。不要用 `git add -f` 強制加入這些檔案。測試與介面示範使用 `example.com` 假帳號，無須帶入真實帳號資料即可建置和測試。

## 開發與驗證

在專案目錄執行 `powershell -ExecutionPolicy Bypass -File .\build.ps1`，使用 Windows 內建的 .NET Framework C# 編譯器。沒有 NuGet 相依套件。原始碼為 `Core.cs`、`DesktopLauncher.cs`、`ProcessInspector.cs`、`SwitchWorkflow.cs`、`App.cs` 和內嵌的 `MainWindow.xaml`。程序診斷使用 Windows WMI，只讀取程序名稱、PID、父程序、工作階段與執行檔路徑，不讀取命令列或記憶體內容；無法確認程序狀態時停止切換。

執行假帳號測試（不會讀取或修改真實登入）：

```powershell
$test = Start-Process -FilePath .\tests\bin\CodexAccountSwitcher.exe -ArgumentList '--self-test', "$PWD\test-results.json" -WindowStyle Hidden -PassThru -Wait
Get-Content .\test-results.json
```

測試必須在已載入 Windows 使用者設定檔的一般環境執行；某些沙盒無法使用 DPAPI。涵蓋加解密、重複帳號與工作區、憑證更新、新登入覆蓋舊登入、復原、程序執行阻擋、競爭更新、設定限制、備份寫入失敗、JSON-RPC 模擬登入及取消清理。

測試也涵蓋套件啟動目標解析、版本升級後的啟動識別、程序來源分類，以及完整等待協調流程（包括 VS Code 殘留、程序重新出現、取消、逾時、診斷失敗及套用失敗不啟動）。

測試與介面預覽使用虛構帳號、合成程序資訊與測試目錄，不需要真實帳號、特定電腦名稱或固定安裝位置。這些自動測試不涵蓋真實帳號切換後的完整桌面登入流程。

參考官方文件：[登入快取與驗證](https://learn.chatgpt.com/docs/auth)、[App Server 登入協定](https://learn.chatgpt.com/docs/app-server)。

Windows 啟動機制：[ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication)、[PackageFamilyNameFromFullName](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-packagefamilynamefromfullname)。
