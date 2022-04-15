#!/bin/bash
red='\033[0;31m'
green='\033[0;32m'
yellow='\033[0;33m'
plain='\033[0m'
cur_dir=$(pwd)
# check root
[[ $EUID -ne 0 ]] && echo -e "${red}错误：${plain} 必须使用root用户运行此脚本！\n" && exit 1
# check os
if [[ -f /etc/redhat-release ]]; then
    release="centos"
elif cat /etc/issue | grep -Eqi "debian";then
    release="debian"
elif cat /etc/issue | grep -Eqi "ubuntu";then
    release="ubuntu"
elif cat /etc/issue | grep -Eqi "centos|red hat|redhat";then
    release="centos"
elif cat /proc/version | grep -Eqi "debian";then
    release="debian"
elif cat /proc/version | grep -Eqi "ubuntu";then
    release="ubuntu"
elif cat /proc/version | grep -Eqi "centos|red hat|redhat";then
    release="centos"
else
    echo -e "${red}未检测到系统版本，请联系脚本作者！${plain}\n" && exit 1
fi
arch=$(arch)
if [[ $arch == "x86_64" || $arch == "x64" || $arch == "amd64" ]]; then
  arch="amd64"
elif [[ $arch == "aarch64" || $arch == "arm64" ]]; then
  arch="arm64"
else
  arch="amd64"
  echo -e "${red}检测架构失败，使用默认架构: ${arch}${plain}"
fi
echo "架构: ${arch}"
if [ $(getconf WORD_BIT) != '32' ] && [ $(getconf LONG_BIT) != '64' ] ; then
    echo "本软件不支持 32 位系统(x86)，请使用 64 位系统(x86_64)，如果检测有误，请联系作者"
    exit -1
fi
os_version=""
# os version
if [[ -f /etc/os-release ]]; then
    os_version=$(awk -F'[= ."]' '/VERSION_ID/{print $3}' /etc/os-release)
fi
if [[ -z "$os_version" && -f /etc/lsb-release ]]; then
    os_version=$(awk -F'[= ."]+' '/DISTRIB_RELEASE/{print $2}' /etc/lsb-release)
fi
if [[ x"${release}" == x"centos" ]]; then
    if [[ ${os_version} -le 6 ]]; then
        echo -e "${red}请使用 CentOS 7 或更高版本的系统！${plain}\n" && exit 1
    fi
elif [[ x"${release}" == x"ubuntu" ]]; then
    if [[ ${os_version} -lt 16 ]]; then
        echo -e "${red}请使用 Ubuntu 16 或更高版本的系统！${plain}\n" && exit 1
    fi
elif [[ x"${release}" == x"debian" ]]; then
    if [[ ${os_version} -lt 8 ]]; then
        echo -e "${red}请使用 Debian 8 或更高版本的系统！${plain}\n" && exit 1
    fi
fi
install_base() {
    if [[ x"${release}" == x"centos" ]]; then
        yum install wget curl tar -y
    else
        apt install wget curl tar -y
    fi
}
config_after_install() {
    read -p "请输入服务器地址:" config_serverurl
    echo -e "${yellow}服务器地址将设定为:${config_serverurl}${plain}"
    /usr/local/shandian_status/client ${config_serverurl}
}
install_client(){
    echo -e "开始安装 闪电监控 客户端 "
    last_version=$(curl -Ls "https://api.github.com/repos/zzzzpro/shandian/releases/latest" | grep '"tag_name":' | sed -E 's/.*"([^"]+)".*/\1/')
    if [[ ! -n "$last_version" ]]; then
        echo -e "${red}检测 闪电监控 版本失败，可能是超出 Github API 限制，请稍后再试，或手动指定 闪电监控 版本安装${plain}"
        exit 1
    fi
    echo -e "检测到 闪电监控 最新版本：${last_version}，开始安装"
    wget -N --no-check-certificate -O /usr/local/shandian_status-${arch}.tar.gz https://github.com/zzzzpro/shandian/releases/download/${last_version}/shandian_status-${arch}.tar.gz
    if [[ $? -ne 0 ]]; then
        echo -e "${red}下载 闪电监控 失败，请确保你的服务器能够下载 Github 的文件${plain}"
        exit 1
    fi
    cd /usr/local
    #开始解压
    #if [[ -e /usr/local/shandian_status/ ]]; then
    #  rm /usr/local/shandian_status/ -rf
    #fi
    systemctl stop shandian_status
    tar zxvf shandian_status-${arch}.tar.gz
    rm shandian_status-${arch}.tar.gz -f
    cd shandian_status
    chmod +x client
    cp -f shandian_status.service /etc/systemd/system/
    config_after_install
    systemctl daemon-reload
    systemctl enable shandian_status
    systemctl start shandian_status
    echo -e "${green}闪电监控 安装成功${plain}"
}
echo -e "${green}开始安装${plain}"
install_base
install_client
