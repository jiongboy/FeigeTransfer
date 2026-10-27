# Feige 下载官网（静态页）

部署到更新服务器 Mac mini：

```
/usr/local/var/www/feige-site/     ← 内容
http://192.168.50.229:8080/feige/  ← 访问地址
```

页面在浏览器里读取 `/updates/windows/<channel>/feige-windows-update.json`，
优先 `stable`，没有正式版时回落到 `preview`，因此发布新版本后无需改页面。

## 更新方式

```bash
# 修改 Project/Windows/site/index.html 后同步到服务器
scp -r Project/Windows/site/index.html Project/Windows/site/assets songli@macmini:/usr/local/var/www/feige-site/
```

nginx 的 `location /feige/` 与 `/updates/` 一样只放行私有网段，配置见
`/usr/local/etc/nginx/servers/macmini.conf`。
