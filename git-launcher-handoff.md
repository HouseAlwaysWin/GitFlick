# Handoff：個人用 Git Launcher（Avalonia，Windows-first）

> 這份文件是給 Claude Code 的完整規格。你（Claude Code）看不到原始討論，所有必要的決策與脈絡都寫在這裡。
> **請照 Milestone 順序實作，每完成一個模組先確保 app 仍能啟動、既有功能沒壞，再往下做。**
> **若你想偏離「範圍邊界」那一節列出的任何一點，先停下來問，不要自作主張擴充功能。**

---

## 0. 這是什麼、以及最重要的設計哲學

一個**系統匣常駐、全域快捷鍵瞬間喚出**的極簡 Git 客戶端,專供作者個人使用。動機不是做一個更強的 GitKraken,而是做一個**符合個人操作品味的極簡工具**:秒開、鍵盤驅動、只做需要的功能。

**核心哲學 —— 贏在克制,不在功能:**
市面工具(Sourcetree/GitKraken)複雜或笨重,是因為要服務所有人。這個工具**刻意只做少數幾件事**。請主動抵抗「順手多加一個功能」的衝動:
- 不做 gitflow 面板、不做一堆 host 整合(Jira/Trello 等)、不做設定地獄。
- UI 極簡、鍵盤優先、預設值即可用。
- 「少即是快」是這個專案的第一原則。有疑慮時,選更少的那個。

作者是資深 C#/.NET 開發者,技術棧全熟。程式碼註解與識別字用英文,規格與溝通用繁體中文即可。

---

## 1. 技術棧與硬性約束

| 項目 | 選擇 | 說明 |
|---|---|---|
| 語言/框架 | C# 14 + **Avalonia UI 12**(NuGet 現為 12.1.x) | 桌面優先。**注意 v12 破壞性變更,見 §1.1** |
| Target | **`net10.0`**(Windows-only 可接受) | .NET 10 是 Avalonia 12 官方建議;未來若把架構移植到 Android 也要 net10,統一省事 |
| MVVM | **CommunityToolkit.Mvvm** | 走 source generator,**AOT 友善**。**不要用 ReactiveUI**(反射重、對未來 NativeAOT 不友善) |
| Git 溝通 | **Shell out 給 git CLI** | **不要用 LibGit2Sharp**。理由見 §3 |
| Diff 語法高亮 | **AvaloniaEdit**(+ `AvaloniaEdit.TextMate`) | 別自己造 highlighter |
| 設定持久化 | JSON(`System.Text.Json`) | repo 收藏清單、快捷鍵等 |
| 平台 | Windows 優先 | 全域熱鍵用 Win32 API,見 §4 |

**前置條件:** 系統需已安裝 git 且在 PATH。啟動時偵測 git,找不到就顯示明確錯誤並允許在設定裡手動指定 git 執行檔路徑。

**AOT 備註(非 MVP 必要,但別擋路):** 未來可能上 Avalonia 12 的 NativeAOT + trimming 來壓啟動與記憶體。因此:MVVM 用 CommunityToolkit(已 AOT 友善),避免引入重度反射的相依。現在**不需要**真的開 AOT,只要別做出擋住這條路的設計。

### 1.1 Avalonia 12 破壞性變更(從 v11 範例/記憶寫 code 會踩雷,務必留意)

這是新版本,很多網路教學和你記憶中的 API 是 v11 的。動工前先吸收這幾點:

- **Compiled bindings 現在預設開啟**(`AvaloniaUseCompiledBindingsByDefault` 預設 true)。**每個 View 要加 `x:DataType`**,binding 走 compiled binding;真正需要動態的才用 `{ReflectionBinding ...}`。對本專案是好事:compiled binding 更 AOT 友善,跟技術取向一致。
- **視窗裝飾 API 整組換掉(對本專案特別重要)。** 這種 launcher 式視窗很可能想要無邊框 / 自訂 chrome。v12:`SystemDecorations` → **`WindowDecorations`**;`ExtendClientAreaChromeHints` 已移除,改用 `WindowDecorations` 搭配 `ExtendClientAreaToDecorationsHint`;`TitleBar`/`CaptionButtons` 等型別移除,改用新的 `WindowDrawnDecorations`。自訂標題列的 hit-testing 寫法跟 v11 不同,別照舊教學抄。
- **DevTools 改名:** `Avalonia.Diagnostics` 套件移除 → 改用 `AvaloniaUI.DiagnosticsSupport`,`AttachDevTools()` → **`AttachDeveloperTools()`**。
- **Clipboard / drag-drop:** `IDataObject` / `DataObject` → `IDataTransfer` / `IAsyncDataTransfer` / `DataTransfer`。
- 套件升級時把所有 `Avalonia.*` 一起升到同一個 12.x 版本,不要混版。

---

## 2. 專案骨架(第一步先建這個)

用 Avalonia MVVM 範本起專案(先 `dotnet new install Avalonia.Templates` 取得 v12 範本,再建 net10.0 專案)。建議結構:

```
GitLauncher/                      # 工作名,作者可改
  GitLauncher.csproj
  Program.cs                      # 進入點,tray-first(不預設開主視窗)
  App.axaml / App.axaml.cs
  Services/
    IGitService.cs                # git CLI 封裝介面
    GitService.cs                 # 實作(shell out)
    IGlobalHotkeyService.cs       # 全域熱鍵介面(平台無關)
    Win32GlobalHotkeyService.cs   # Windows 實作(RegisterHotKey)
    ISettingsService.cs / SettingsService.cs
  Models/                         # GitStatusEntry, CommitInfo, BranchInfo, DiffHunk...
  ViewModels/
  Views/
```

**重點:平台相依的東西(全域熱鍵)藏在介面後面**(`IGlobalHotkeyService`),Win32 P/Invoke 只出現在 `Win32GlobalHotkeyService`。作者過去做過 `IDialogService` 的抽象,沿用同樣風格。這樣未來要跨平台只需換實作。

---

## 3. 已拍板的架構決策(請勿重新評估)

**用 git CLI(shell out),不用 LibGit2Sharp。** 理由:
- git 是唯一真相來源,功能 100% 覆蓋,不會撞到 binding 未支援的牆。
- **直接沿用使用者現有的 credential helper / SSH 設定**,認證這塊幾乎不用自己重造(最容易被低估、最痛,能白嫖)。
- 缺點是要 parse porcelain 輸出,但格式穩定、有專為機器設計的版本。

**Status 一律用 `git status --porcelain=v2 --branch`。** v2 是為機器設計、格式穩定。不要 parse 給人看的預設輸出。

**GitService 的 process 封裝要求:** 非同步、支援 cancellation、分開拿 stdout/stderr、檢查 exit code、每次呼叫可指定 working directory(每個 repo 不同)。長時間操作(push/pull/fetch)要能回報進行中狀態。

---

## 4. 專屬於這個專案的坑(務必先處理,否則後面每個功能零星壞給你看)

### 4.1 CJK 路徑與 quotepath(最重要)
作者用繁體中文,檔名和 commit message 會有 CJK 字元。git 預設 `core.quotepath=true`,會把非 ASCII 路徑輸出成八進位跳脫(如 `\344\275\240`),parser 會爆掉。

**對策:** GitService 對每個 repo 執行 git 前,確保帶上 `-c core.quotepath=false`(或開 repo 時設定一次)。

### 4.2 UTF-8 解碼(同樣關鍵)
**必須用 UTF-8 解碼 git 的 stdout/stderr**,不要吃到 Windows 系統的 Big5 / ANSI code page,否則中文全部變亂碼。啟動 process 時明確設定 `StandardOutputEncoding = Encoding.UTF8`(stderr 同)。必要時對 git 設 `i18n.logOutputEncoding=UTF-8`。

### 4.3 全域熱鍵(唯一真正有坑的模組)
Avalonia **沒有**全域熱鍵(它只在自己有焦點時收輸入)。Windows 專用走 `RegisterHotKey`(P/Invoke `user32.dll`):
- 需要一個 **message-only 隱藏視窗**(`HWND_MESSAGE`)來接 `WM_HOTKEY`,並跑它自己的訊息迴圈(獨立執行緒)。
- 收到 `WM_HOTKEY` 後,marshal 回 UI thread 顯示主視窗。
- 全部包在 `Win32GlobalHotkeyService` 裡,實作 `IGlobalHotkeyService`。
- 熱鍵組合可設定(存 JSON)。註冊失敗(被別的程式佔用)要能優雅回報,不要 crash。

### 4.4 Tray 常駐 = 視窗關閉改為隱藏
Avalonia 有內建 `TrayIcon`(v12 仍在)。攔截主視窗的關閉事件,改成**隱藏**而非結束 process,讓程式續命在系統匣。真正結束只從 tray 選單的「Exit」走。「快速啟動」的正解是 **process 常駐、喚出零延遲**,不是優化冷啟動。若主視窗要做無邊框/自訂 chrome,用 §1.1 的 v12 `WindowDecorations` API,別照 v11 教學。

---

## 5. 模組拆解與實作順序

七個模組,照順序做。標「便宜/中/貴」是相對工作量。

### ① 外殼:tray 常駐 + 全域快捷鍵 + 視窗喚出/隱藏 —— 中(坑集中在此)
所有東西都住在這裡,必須先做。
- Tray-first 啟動(Program.cs 不預設開主視窗)。
- `TrayIcon` + 選單(Show / Exit)。
- 關閉 = 隱藏(§4.4)。
- `IGlobalHotkeyService` + Win32 實作(§4.3)。
- **驗收:** 程式啟動後只在系統匣;按下設定的全域熱鍵,主視窗**零延遲**出現並取得焦點;再按或失焦可隱藏;process 全程不重啟。

### ② 常用 repo 收藏 + 命令面板 —— 便宜
喚出後焦點直接落在一個 fuzzy 搜尋框。
- 列出 pin 的 repo(存 JSON)。可新增/移除收藏(給個資料夾選擇)。
- 打字做模糊過濾,↑↓ 選擇,Enter 進入該 repo 的工作區畫面。
- **驗收:** 熱鍵喚出 → 搜尋框已聚焦 → 幾個鍵就能切到任一收藏 repo 並進入其 staging 畫面,全程不碰滑鼠。

### ③ Git CLI 層 —— 中(app 的地基)
`IGitService` / `GitService`。先處理好 §4.1–4.2 的 encoding/quotepath。
- Process 封裝(§3 的要求)。
- `git status --porcelain=v2 --branch` 解析成 `GitStatusEntry`(路徑、staged/unstaged 狀態、類型)。
- 基礎指令 wrapper:add/reset(stage/unstage)、commit、push、pull/fetch、branch 列表、checkout、merge、stash push/pop/list。
- **驗收:** 能對一個含中文檔名的測試 repo 正確讀出 status(無亂碼、無八進位跳脫),並成功執行一次 stage → commit。

### ④ 工作區操作 UI:stage / commit / push / branch / merge / stash —— 中
- Status 清單分「已暫存 / 未暫存」兩區,可單檔或全部 stage/unstage。
- Commit(訊息輸入框 + commit 按鈕)。
- Push / pull / fetch,長操作顯示進行中 + 完成/錯誤回饋。
- Branch:列表 + 建立 / 切換 / 刪除;merge 選來源分支。
- Stash:push / pop / list。
- **驗收(里程碑 M1,見 §6):** 做到這裡 + ⑤,就是一個每天能取代 80% 操作的工具。

### ⑤ 檔案比較(diff viewer)—— 中
- 先做 **unified inline diff**:解析 `git diff`(未暫存)與 `git diff --cached`(已暫存)的 hunk,上色 +/- 行。
- 用 **AvaloniaEdit + TextMate** 做語法高亮(別自己造)。
- 之後再升級:side-by-side、行內 word-level。
- 大檔要虛擬化,先求堪用,別一開始拼 GitKraken 級。
- **驗收:** 在 status 清單點任一變更檔,右側顯示可讀、上色正確的 diff。

### ⑥ commit 歷史清單 + 檔案歷史 —— 便宜(③⑤ 做好後幾乎免費附送)
作者標為「最重要」之一,好消息是它幾乎完全重用前面的成果。
- Commit 清單:`git log`(當前分支)列成 list,顯示 hash/作者/時間/訊息;點一筆用 ⑤ 的 diff viewer 顯示該 commit 的變更。
- **檔案歷史(核心需求):** 對選定檔案跑 `git log --follow -- <path>`,列出「這個檔案動過哪些 commit」;點某 commit 顯示那次對該檔的改動(重用 ⑤)。`--follow` 讓改名也能追。
- (加分)行級歸屬:`git blame -L <a>,<b> -- <path>`,顯示某幾行最後是誰、哪個 commit 改的。
- **驗收:** 選一個檔案,能看到它歷來被哪些 commit 動過,點進去看得到每次的 diff。

### ⑦ 分支樹狀圖 —— 貴(全專案唯一的大頭;有現成範本可抄)
作者標為「最重要」之二。誠實說,渲染「所有分支、任意拓撲、幾千 commit 的即時 lane 佈局 + 虛擬化」是獨立工程題。**請照難度階梯,先做 Level 1,再視情況上 Level 2,不要一開始就衝 Level 3。**

- **Level 1(便宜、日常夠用,先做這個):** 當前分支的線性歷史清單 + 側邊分支列表。無多 lane 繪圖。覆蓋 90% 的「發生過什麼」需求 —— 其實就是 ⑥ 的 commit 清單。
- **Level 2(作者想要的「樹」,但收斂):** 畫出 lane 的 graph 欄,**但限定範圍**(當前分支 + 直接祖先,或有界 commit 數 + 虛擬化)。**額外做一個 `--first-parent` 收合檢視**:在 main 上以 `git log --first-parent` 呈現,每列 = 一次 merge = 一條分支,把糾纏的圖收成一條乾淨的欄位,用來回答「哪條分支合進 main 出問題」。
- **Level 3(GitKraken 級全景 DAG):** 全分支、完整 lane 演算法 + 虛擬化。最貴,個人用途通常不值得。**除非作者明確要求,否則不要做。**

**關鍵外部資源 —— 不要自己發明 lane 演算法:**
[**SourceGit**](https://github.com/sourcegit-scm/sourcegit) 是開源的 Avalonia + C# git 客戶端,**本身就有畫 commit graph**。直接研讀它的原始碼,lane 佈局與 graph 繪製那段可整段參考,把 ⑦ 從「未知硬題」變成「有範本可抄的中等工作」。這是本專案最重要的參考來源。

- **驗收(Level 2):** 能看到有限範圍的分支 lane 圖;能一鍵切到 `--first-parent` 收合檢視。

---

## 6. 里程碑(每個都要是「能自己用」的可交付狀態)

- **M1 —— 每天能用的工具:** 模組 ①②③④⑤。tray 常駐 + 熱鍵喚出 + 命令面板選 repo + stage/commit/push/branch/merge/stash + 可讀的 diff。**這是第一個該「發佈給自己用」的點。**
- **M2 —— 檔案歷史:** 模組 ⑥。commit 清單 + `--follow` 檔案歷史 +(可選)blame。
- **M3 —— 分支圖:** 模組 ⑦ Level 1 → Level 2(抄 SourceGit)。Level 3 留待明確需求。

做完 M1 就停下來讓作者實際用一陣子,再決定 M2/M3 的投入。

---

## 7. 範圍邊界(明確的 Non-goals —— 偏離前先問)

- **不做** Level 3 全景 DAG(除非作者明確要求)。
- **不做** gitflow 面板、PR 管理、issue tracker 整合(Jira/Trello/GitHub PR 等)。
- **不做** 使用者帳號/雲端同步/團隊協作功能。
- **不做** 華麗的設定頁;設定越少越好,能用預設就用預設。
- **不重造** 認證:靠 git CLI 沿用系統既有 credential/SSH 設定。
- **不用** ReactiveUI、不用 LibGit2Sharp(理由見 §1、§3)。
- diff viewer、branch graph **先求堪用**,不要為了追平 GitKraken 的視覺而過度投入。

有任何「順手多加一點」的念頭,預設答案是不加。這個工具的價值在克制。

---

## 8. 常用 git 指令對照(實作各功能時的參照)

| 功能 | 指令 |
|---|---|
| Status(機器解析) | `git status --porcelain=v2 --branch` |
| 修正 CJK 路徑 | 帶 `-c core.quotepath=false` |
| Stage / Unstage | `git add -- <path>` / `git reset -- <path>` |
| 未暫存 / 已暫存 diff | `git diff -- <path>` / `git diff --cached -- <path>` |
| Commit 清單 | `git log --pretty=...`(當前分支) |
| 檔案歷史(含改名) | `git log --follow -- <path>` |
| 行級歸屬 | `git blame -L <a>,<b> -- <path>` |
| first-parent 收合檢視 | `git log --first-parent` |
| 某 commit 屬於哪條分支 | `git branch --contains <sha>` / `git name-rev <sha>` |
| 追某符號的歷史(加分) | `git log -S "<symbol>"` / `git log -G "<regex>"` |

---

## 9. 給 Claude Code 的執行提醒

1. 先建 §2 的專案骨架並確認能啟動(空的 tray app)。
2. 嚴格按 ①→⑦ 順序,每個模組完成後跑一次確認沒回歸。
3. §4 的四個坑在對應模組動工**之前**先處理掉。
4. 到 ⑦ 之前先去讀 SourceGit 原始碼再動手畫圖。
5. 任何觸及 §7 範圍邊界的偏離,先問再做。
