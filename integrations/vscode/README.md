# VS Code 選用實驗整合

此整合讓帳號切換器查詢 **VS Code 實際使用的同一個 Codex 後端**，正常中止回合，並在確認閒置後暫停後端。預設不啟用；安裝 VSIX 本身不會修改 Codex 設定或重載既有視窗。

目前限本機 Windows VS Code 與原生 Windows Codex 後端。WSL、SSH、遠端擴充套件宿主及未透過本整合啟動的視窗不支援自動控制。

## 啟用

1. 保留發行資料夾中的 `CodexAccountSwitcher.exe` 與 `ManagedBridge.exe`，兩者必須位於同一資料夾。
2. 在 VS Code 執行 **Extensions: Install from VSIX…**，選取發行資料夾中的 `codex-account-switcher-vscode.vsix`。
3. 執行 **Codex Account Switcher: Enable Experimental Integration**，選取 `ManagedBridge.exe`，閱讀並確認啟用提示。
4. 等待目前 Codex 工作結束，再按 **Reload Window**。只有重新啟動後由代理持有的後端才可控制。

此功能使用官方標為 development-only 的 `chatgpt.cliExecutable`。該設定具有 application scope，可能影響這個 VS Code 安裝的其他視窗與設定檔下次啟動 Codex 的方式。整合透過 VS Code 設定 API 更新值，保留 `settings.json` 的註解和其他設定。

每次啟用都由使用者執行命令。切換器不會替已開啟的 VS Code 視窗插入代理，不修改官方擴充套件程式，也不終止 `Code.exe` 或共用擴充套件宿主。

## 暫停與恢復

手動指定帳號切換或切回上一個帳號時，會等待所有回合、待處理要求及背景工作結束，再自動正常退出本整合持有的 Codex 後端並套用帳號。「允許正常中止回合」選項只適用於自動監看切換，不會讓手動切換中止進行中的對話。


正常中止會等待 Codex 回報回合結束。暫停前會再次查詢該後端全部已載入對話與背景工作；狀態未知、背景工作仍在執行或尚有等待中的請求時，不會將後端視為可安全暫停。

恢復會要求配套擴充套件 **重載對應的 VS Code 視窗**，讓官方 Codex 擴充套件重新建立連線。這不只是重畫 Codex 面板；未送出的草稿及擴充套件暫存狀態可能受到影響。代理不會自動重送先前的訊息或工具操作，也不保證跨帳號續話與歷史的可用性。

後端沒有在期限內自然退出時，切換器顯示未知／需要處理，不升級成強制終止。若配套擴充套件未載入，恢復旗標不會造成自動重載；可以手動執行 **Developer: Reload Window**。

## 停用與復原

執行 **Codex Account Switcher: Disable Experimental Integration**：

- 立即撤銷受管後端的中止與暫停控制權；既有 stdio 傳輸仍可繼續服務。
- 若 `chatgpt.cliExecutable` 仍指向本整合，還原啟用前的值；原本未設定時會移除該設定。若使用者已改為其他值，停用不覆寫該值。
- 在工作結束後重載視窗，讓 Codex 改回原本的啟動方式。已暫停的 Codex 後端需要重載才能重新連線。

停用後不會自動重載視窗。切換器仍可對自己已暫停的後端提出恢復要求，但停用的配套擴充套件不會執行重載。重新啟用會建立新的控制授權；舊的代理必須重載後才能重新取得控制權。

若 Codex 擴充套件升級、發行資料夾搬移或啟動設定不相容，先停用並重載，再重新執行啟用命令。正在使用不同後端版本的受管視窗尚未結束前，整合拒絕覆寫共用啟動設定。

## 本機資料

設定與執行階段目錄位於 `%LOCALAPPDATA%\CodexAccountSwitcher\integrations\vscode\`，只授權目前 Windows 使用者。它們包含工具路徑、程序識別與啟動時間、控制狀態等本機中繼資料，應留在個人電腦上。

代理只在記憶體中轉發協定，不把對話、提示、工具輸出或憑證寫入自己的記錄。官方 Codex 本身的資料儲存行為仍由官方程式處理。

## 驗證範圍

既有 6 組真實 Windows named pipe＋模擬後端測試及 9 組 companion 模擬測試通過，涵蓋權限、路由、中止、自然退出、背景工作分頁、逾時、授權撤銷與設定還原。

本輪另以 VS Code 1.143.1、官方 Codex 擴充套件 26.1007.21434 及其真正 backend，在預先配置的隔離視窗內，從實際 webview 啟動合成回合，驗證忙碌時拒絕暫停、正常中止、stdin EOF 自然退出，以及 companion 重載後建立新 host／backend／generation 並恢復閒置。編輯器測試檔案與分頁保留，Codex 側欄可開啟；**原對話是否恢復尚未確認，真實帳號切換與跨帳號續聊尚未驗證**。

此實驗使用本機合成模型回覆，未使用真實登入；預先設定的測試授權狀態不代表首次啟用操作已實測。工具執行、核准等待、背景命令及多視窗競態仍需實際驗收。詳細範圍見 [控制介面驗證](../../docs/runtime-control-validation.md)。

官方參考：[IDE 設定](https://learn.chatgpt.com/docs/ide/settings)、[App Server 協定](https://learn.chatgpt.com/docs/app-server)。
