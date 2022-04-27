# 闪电云探针监控面板

## 客户端安装与升级
```
bash <(curl -Ls https://raw.githubusercontent.com/zzzzpro/shandian/master/client/install.sh)
```
客户端配置格式：http://140.238.16.23:9999
建议使用域名

## 服务端安装
```
bash <(curl -Ls https://raw.githubusercontent.com/zzzzpro/shandian/master/server/install.sh)
```

### 宝塔面板安装
反代http://localhost:9999 及 ws
```
location / {proxy_redirect off;proxy_set_header Host $host;proxy_set_header X-Real-IP $remote_addr;proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;proxy_pass http://127.0.0.1:9999;}

location /ws {proxy_redirect off;proxy_intercept_errors on;proxy_pass http://127.0.0.1:9999;proxy_http_version 1.1;proxy_set_header Upgrade $http_upgrade;proxy_set_header Connection "upgrade";proxy_set_header Host $http_host;proxy_read_timeout 300s;}
```

## 演示站点 http://140.238.16.23:9999/ 

## 不定时更新
## todo
 1. 在线率统计
 2. 客户端兼容window
