校园助手实现参考

功能与接口参考：
https://github.com/5dbwat4/ZJU-live-better
查看提交：f85f59f45b5d6c1fe861808178387264c37ad346
主要参考路径：courses.zju/reliableTodolist.js、courses.zju/autosign.js、lib.zju/bookList.js。
认证协议参考：https://github.com/5dbwat4/login-ZJU（login-zju 1.0.9）。
Windows 定位 ABI：本机 Windows SDK 10.0.26100.0 locationapi.h；Microsoft ILocation 文档。

本版使用独立 C# 适配器，没有运行或打包上游 Node 脚本，没有加入钉钉推送服务。上游 ZJU-live-better 使用 GPL-3.0，login-ZJU 使用 MIT；其各自许可由上游仓库维护。现有 MathLive、WebView2、WpfMath 等许可保留在 licenses。

0.7.3 验证：2026-09-22 直接检查公开登录跳转链，确认 courses → identity 的 CAS broker → zjuam → identity → courses。identity.zju.edu.cn 已加入精确主机列表，未知域名仍禁止。
用户授权账号的只读测试已通过统一认证、课程事项读取和签到列表读取；测试进程因 Windows TLS 环境限制使用临时 Python/OpenSSL 传输，运行正式 C# 登录/解析/合并代码。传输脚本与凭据不进入发布包。没有执行签到或续借。
学校的 homework/submission-status 接口返回 404：保持上游允许其失败后继续读取的行为，同时明确标记完成状态未知、保留本地已有完成标记，而不是伪装完整同步。

2026-10-21 图书借阅修复：移动接口 api.lib.zju.edu.cn 连接超时，读取改用官网公开入口 https://opac.zju.edu.cn/zjusso/ 的统一认证与 Aleph 当前借阅、历史借阅页面。按读者概况中的管理库和数量读取当前列表；历史记录独立缓存，不生成待办。官网历史页若仅展示总数的一部分，界面明确说明范围。未知页面、缺失字段和数量冲突均报错并保留缓存。
本机系统代理下 .NET 认证请求超时，直连返回 HTTP 200。CampusClient 仅访问精确白名单中的学校域名，连接使用直连；没有修改系统代理。原生 .NET HttpClient + 正式登录及解析代码实测：当前借阅 0 本，历史借阅 0 条。没有执行续借或签到。
非空列表结构依据官网公开的 bor-loan-head/body 与 bor-history-loan-head/body/tail 模板；使用合成图书记录测试日期、重复借阅和数量校验。本账号没有非空借阅记录，尚无真实非空数据验证。旧版源文件及程序位于 update-backups/library-history-20261021。
