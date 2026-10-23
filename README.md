# Codex 帳號切換器

Windows 原生、繁體中文的本機帳號管理小工具。使用 Windows 10 / 11 x64 與 .NET Framework 4.8，無須安裝 Python、Node.js 或額外套件。從 GitHub 取得原始碼後，雙擊根目錄的「Codex帳號切換器.cmd」即可開啟；若缺少執行檔或設定檔，啟動器會先自動建置。也可手動執行 `powershell -ExecutionPolicy Bypass -File .\build.ps1`，再開啟 `dist-v1.2.0\CodexAccountSwitcher.exe`。

## v1.2.0：手動排序與額度判斷

選取帳號後按「上移／下移」調整優先順序，越上方越優先。順序會隨帳號庫加密保存，重新開啟仍保留；新增帳號排在最後，重新登入、改名及更新額度不會改變順位。排序不會切換目前登入。

各帳號新增「額度可用／額度已用完／狀態未知」提示。「選取優先可用帳號」會依目前排序選取第一個已確認有額度的帳號，不會自動套用；仍需按「切換並開啟 Codex」。目前帳號也可被選中。

- 成功回覆只有每週上限且仍有剩餘時，保持可用；不依 Pro／Plus 名稱猜測限制。
- 任一有效的 5 小時或每週額度為零時，判定已用完。
- 查詢失敗、未提供任何適用上限、資料異常，或快照超過一分鐘／已到重設時間時，判定未知，不列入優先選取。缺少某個上限與提供異常資料會分開處理。
- 舊百分比仍可供參考，但不會自行推算額度恢復。請按「更新全部額度」重新確認。

這個版本先完成排序與額度判斷，不會自動輪詢額度、中止對話、關閉程式或接續對話。桌面 App 與 VS Code 的中止／恢復整合驗證記錄見 [控制介面驗證](docs/runtime-control-validation.md)。

## v1.1.0：各帳號的剩餘額度

已儲存的帳號會各自顯示「5 小時上限」與「每週上限」的剩餘百分比、進度條、重設時間及最近查詢時間。開啟工具、儲存目前帳號或新增帳號後會查詢；也可按「更新全部額度」或「重新整理」手動更新。查詢期間可取消，不會切換目前的 Codex 登入。

資料來自官方 [App Server 的 `account/rateLimits/read`](https://learn.chatgpt.com/docs/app-server)。依服務提供的視窗長度區分 5 小時與每週，剩餘量為 `100 - usedPercent`；重設時間以目前電腦時區顯示。未提供的額度會標示「未提供」，不視為無限、0% 或 100%。這些是查詢當下的快照；到達預計重設時間後仍需重新查詢，不會自行推算額度恢復，也不代表其他模型或工作區限制已解除。

這項功能需要支援上述介面的 Codex CLI／桌面版。憑證過期、網路錯誤、服務拒絕或版本不支援時，個別帳號會顯示提示；若提示登入失效，請用「新增帳號」重新登入同一帳號後再查詢。每個帳號的查詢最多等待 30 秒，不會為了取得額度自動執行帳號切換或使用額度重設點數。

## v1.0.2 更新

修正「結束 ChatGPT 後持續等待，手動開啟仍是原帳號」時提示不明確的問題。若 VS Code 的 Codex 擴充功能仍在執行，切換器會等待它退出，尚未進入帳號套用步驟。新版會區分並列出桌面 App、VS Code、其他 IDE、CLI 或未知程序，以及 PID 和應關閉的來源。

等待中的帳號尚未套用，請等工具完成，不要先手動重新開啟 Codex。等待最多 5 分鐘，逾時或取消都不會進入寫入步驟。所有相關程序退出後需通過一秒安靜期與再次檢查，才會保存、套用、讀回核對並啟動。工具不會強制關閉 VS Code 或終止你的工作。

更新時，先取消舊版等待並關閉舊版切換器，再開啟根目錄的 `Codex帳號切換器.cmd`；它會啟動 `dist-v1.2.0` 的新版，帳號庫沿用。

## v1.0.1 更新

修正自動開啟桌面 App 出現「處理程序沒有套件識別資料」的問題。Microsoft Store / MSIX 安裝現在會從套件資訊解析正確的 Application ID，交由 Windows `IApplicationActivationManager` 啟動，保留套件身分；啟動失敗時不會再直接執行套件內的 EXE。一般非套件程式仍使用原本的 EXE 啟動方式。

此修正已包含在 v1.0.2。已儲存帳號會沿用，不需重新新增。

## 使用方式

1. 開啟工具，按「儲存目前帳號」收藏正在使用的 ChatGPT 帳號。
2. 按「新增帳號」，在官方瀏覽器登入頁選擇另一個帳號。若瀏覽器自動選到原帳號，在登入頁切換使用者。新增帳號不會更動目前 Codex 登入，需再按「切換並開啟 Codex」才會套用。
3. 可先使用「上移／下移」排列優先順序，再手動選取帳號或按「選取優先可用帳號」，最後按「切換並開啟 Codex」。
4. 依工具顯示的來源完成退出：若列出 **VS Code，先儲存工作並關閉所有 VS Code 視窗**，或停止其 Codex 擴充功能直到對應程序退出；再從 ChatGPT / Codex 選單選「結束 / Quit」，也結束仍在執行的 Codex CLI。**只按視窗右上角 X 可能仍留在系統匣。** 程序全部結束後，工具會自動套用帳號並重新開啟。
5. 在 Codex 的帳號選單確認切換結果。需要返回時按「切回上一個」。

「重新命名」可標記個人、工作、研究等用途。「移除」只移除此工具保存的帳號及其復原副本，不會登出目前 Codex，也不會撤銷伺服器上的登入。「取消」可中止等待切換或新增登入。登入逾時為 10 分鐘。

## 支援範圍

- 支援 `CODEX_HOME\auth.json` 的 ChatGPT 登入快取；未設定 `CODEX_HOME` 時使用 `%USERPROFILE%\.codex\auth.json`。
- 使用同一快取的 CLI / IDE 會跟著變更帳號，同一時間使用同一個登入帳號。
- 會辨識此電腦目前的 OpenAI.Codex 桌面套件，即使程式名稱已變為 ChatGPT.exe。
- 帳號切換的來源快取不支援 keyring / auto / ephemeral 儲存、API Key、外部存取權杖與受管理的特殊登入流程。遇到已辨識的不相容設定會停止套用；組織政策仍由 Codex 自身執行，本工具不修改政策。
- 帳號清單使用本機憑證中的顯示資訊，沒有驗證遠端登入有效性。存取權杖到期通常由 Codex 自行更新；已撤銷或失效的登入需用「新增帳號」重登。相同帳號的新登入會顯示「新登入，待套用」。
- 切換只更換登入快取，聊天、專案與設定沿用原本的本機資料；各帳號的雲端資料可見性由 Codex 決定。
- 此工具為獨立實作，沒有修改或注入 Codex 桌面 App，也不是官方多帳號 API。未保證日後版本仍採用相同快取格式。

## 本機資料

帳號、名稱、優先順序、登入憑證與上一個登入的復原資料，會以 Windows DPAPI 加密後存於：

`%LOCALAPPDATA%\CodexAccountSwitcher\accounts.dpapi`

帳號庫僅供建立它的 Windows 使用者使用，不能直接搬到其他使用者或電腦。

切換前，工具會保存最新登入與復原副本；備份失敗或偵測到 Codex 仍在執行時會停止。若無法自動啟動桌面程式，可從開始功能表手動啟動。

## 開發與驗證

在專案目錄執行 `powershell -ExecutionPolicy Bypass -File .\build.ps1`，使用 Windows 內建的 .NET Framework C# 編譯器。沒有 NuGet 相依套件。原始碼為 `Core.cs`、`DesktopLauncher.cs`、`ProcessInspector.cs`、`SwitchWorkflow.cs`、`App.cs`、`UsageModels.cs`、`UsageSession.cs` 和內嵌的 `MainWindow.xaml`。程序診斷使用 Windows WMI，只讀取程序名稱、PID、父程序、工作階段與執行檔路徑，不讀取命令列或記憶體內容；無法確認程序狀態時停止切換。

執行假帳號測試（不會讀取或修改真實登入）：

```powershell
$test = Start-Process -FilePath .\tests\bin\CodexAccountSwitcher.exe -ArgumentList '--self-test', "$PWD\test-results.json" -WindowStyle Hidden -PassThru -Wait
Get-Content .\test-results.json
```

測試必須在已載入 Windows 使用者設定檔的一般環境執行；某些沙盒無法使用 DPAPI。涵蓋加解密、重複帳號與工作區、憑證更新、新登入覆蓋舊登入、復原、程序執行阻擋、競爭更新、設定限制、備份寫入失敗、JSON-RPC 模擬登入及取消清理。

測試也涵蓋套件啟動目標解析、版本升級後的啟動識別、程序來源分類，以及完整等待協調流程（包括 VS Code 殘留、程序重新出現、取消、逾時、診斷失敗及套用失敗不啟動）。

排序測試涵蓋上下移動、邊界、舊帳號庫相容、新增／改名／重登後順序，以及儲存失敗時回復原序。額度測試涵蓋只有每週上限、格式異常、過期與重設邊界、按順位挑選及真實 WPF 按鈕行為。

測試與介面預覽使用虛構帳號、合成程序資訊與測試目錄。這些自動測試不涵蓋真實帳號切換後的完整桌面登入流程。

參考官方文件：[登入快取與驗證](https://learn.chatgpt.com/docs/auth)、[App Server 登入協定](https://learn.chatgpt.com/docs/app-server)。

Windows 啟動機制：[ActivateApplication](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-iapplicationactivationmanager-activateapplication)、[PackageFamilyNameFromFullName](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-packagefamilynamefromfullname)。
