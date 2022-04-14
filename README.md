#闪电监控面板


## 客户端安装与升级
```
bash <(curl -Ls https://raw.githubusercontent.com/zzzzpro/shandian/master/client/install.sh)
```

## 服务端安装
```
bash <(curl -Ls https://raw.githubusercontent.com/zzzzpro/shandian/master/server/install.sh)
```

宝塔面板用户需反代http://localhost:5000 及ws
```
location /ws {proxy_redirect off;proxy_intercept_errors on;proxy_pass http://127.0.0.1:5000;proxy_http_version 1.1;proxy_set_header Upgrade $http_upgrade;proxy_set_header Connection "upgrade";proxy_set_header Host $http_host;proxy_read_timeout 300s;}
```
