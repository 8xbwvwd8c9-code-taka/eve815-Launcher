# EveLauncher

815 登入器原始碼，目標為 .NET Framework 4.8。

## 建置

Windows 安裝 .NET SDK 與 .NET Framework 4.8 Developer Pack，執行 `build.bat`。

正式版輸出至 `bin\Release\net48\LinLauncher.exe`；主控台版本輸出至 `bin\test\LinLauncher.exe`。

## 設定

將 `EveLauncher.ini.example` 複製為 `EveLauncher.ini`，再設定用戶端、主檔目錄與 `Encoder.ini` 路徑。伺服器清單由本機 `Encoder.ini` 提供。

清單產生模式需要在程序環境設定 `EVE815_LIST_AES_KEY`，值為 16 個可列印 ASCII 字元。金鑰不放入原始碼或設定檔。
