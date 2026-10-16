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
