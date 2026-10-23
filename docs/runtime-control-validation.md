# Codex 中止與恢復介面驗證

驗證日期：2026-10-24。此文件記錄第一階段的實測範圍與整合限制。

## 結論

桌面 App 與 VS Code 所附的 Codex 後端，都通過隔離環境中的「開始回合 → 正常中止 → 正常退出 → 重啟 → 恢復測試對話」實驗。這證明後端協定可行，**不代表帳號切換器已能控制正在使用的桌面 App 或 VS Code**。

第一階段產品只加入帳號排序、額度判斷及優先選取，尚未加入自動中止、退出、重載視窗或續話。

## 測試版本與環境

| 來源 | 版本 |
| --- | --- |
| 桌面 App 隨附 Codex CLI | 0.163.0-alpha.11.2 |
| VS Code 隨附 Codex CLI | 0.162.0-alpha.17.2 |
| VS Code 擴充套件靜態檢查 | 26.1007.21434 |

每次實驗都使用自行啟動的子程序、獨立的空 `CODEX_HOME`、合成對話與僅監聽 loopback 的模擬 Responses SSE 服務。未讀取真實登入或對話，未關閉現有 Codex App、VS Code 或 CLI。模擬模型請求沒有 Authorization header，測試目錄未建立 `auth.json`；測試期間未觀測到被代理攔截的外網請求。

兩個版本的 JSON Schema 均成功產生，包含中止、恢復與背景 terminal 查詢介面；未找到通用的 app-server shutdown RPC。測試沒有做全系統封包擷取，不將代理觀察擴張為整台電腦沒有其他網路活動。

## 已實測的能力

以下結果在兩版後端皆通過。

實驗回合只包含合成模型輸出，背景 terminal 查詢結果為空；尚未實測中止時仍有工具或背景命令執行的情況。

| 實驗 | 觀察結果 |
| --- | --- |
| 正常中止進行中的回合 | 在模擬模型已開始串流後送 `turn/interrupt`，收到成功回覆，並收到 `turn/completed`，狀態為 `interrupted`。 |
| 自行持有的 stdio 後端正常退出 | 中止完成後關閉該子程序的 stdin，程序自然結束，exit code 為 0。 |
| 重啟並恢復對話 | 新程序使用相同測試資料夾，以 `thread/resume` 恢復合成對話；下一個回合完成，再正常退出。 |
| 獨立 stdio 後端無法控制原回合 | 另一個使用相同測試資料夾的程序，看不到原程序的執行中對話；對原回合的中止請求收到 `thread not found`。 |
| 同一 WebSocket listener 可接受第二個控制連線 | 第二個用戶端可看到同一個執行中的對話並正常中止，原用戶端收到 `interrupted`。 |

WebSocket listener 在用戶端及 stdin 關閉後沒有自行退出。實驗最後僅清理自行持有的測試程序，**未證實 WebSocket 後端的正常退出方法**；此清理方式不會成為產品對外部程序的退出策略。

自建 Unix socket 的測試因目錄權限檢查被拒絕，未完成 proxy 連線驗證。這不代表 Windows 不支援該傳輸方式。

## VS Code 目前版本的程式碼觀察

檢查擴充套件的 `package.json` 與 `out/extension.js`：

- 內部 `interruptTurn` 使用 `turn/interrupt`。
- 擴充套件自行啟動 app-server；公開命令清單未提供 stop、interrupt 或 restart，啟用函式亦未提供外部控制 API。
- 後端結束後會清理連線及待處理請求。非零退出會通知錯誤；正常退出後，後續請求也會因程序不存在而失敗。
- 內部 `codex-app-server-restart` 事件呼叫 `workbench.action.reloadWindow`，未看到後端退出後自動就地重連的流程。

因此只停止 Codex 子程序，通常不會關閉 VS Code 編輯器，但 Codex 功能會斷線。現有恢復路徑會重載 VS Code 視窗，不能承諾只影響單一對話。這些是本機版本的靜態觀察，不是已完成的真實視窗退出／恢復測試。

## 尚未通過的整合門檻

1. 找到能連到**現有桌面 App／VS Code 所使用後端**的支援介面；獨立啟動的額度查詢程序不能替代它。
2. 對程序建立時間、來源、執行環境與對話狀態建立可靠對應，取得完整且近期的工作狀態。
3. 正常中止後，另外確認工具、背景命令、等待核准與其他工作均已結束。中止回合不代表撤銷已完成的操作。
4. 協調後端真正退出、避免擴充套件立即重生程序，再重新驗證登入與額度後切換。
5. 驗證桌面 App 正常退出與重新開啟；VS Code 則驗證重載視窗的影響與登入恢復，不終止 `Code.exe` 或共用擴充套件宿主。
6. 對未知狀態、取消、逾時、新回合競態及恢復失敗提供明確結果，不自動升級成強制終止。

本次測試未使用任何真實帳號，故沒有驗證跨帳號續話。`thread/resume` 在合成對話中成功，不能據此保證跨帳號歷史可見性、登入狀態或操作可安全重送。

## 參考

- [OpenAI App Server：中止、對話狀態、傳輸與恢復介面](https://learn.chatgpt.com/docs/app-server)
- [Codex 桌面 App 指令：Quit 與關閉視窗的區別](https://learn.chatgpt.com/docs/app/commands)
- [Codex IDE 公開命令](https://learn.chatgpt.com/docs/ide/commands)

官方的 Windows Quit 快捷鍵為 Ctrl+Q，但本次未對正在使用的桌面 App 送出該操作；視窗消失也不能代替程序退出確認。
