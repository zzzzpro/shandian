<template>
  <a-layout>
    <a-layout-header class="header" title="探针">
      <div class="logo" />
      <a-menu
        theme="dark"
        mode="horizontal"
        :default-selected-keys="['1']"
        :style="{ lineHeight: '64px' }"
      >
        <a-menu-item key="1">
          首页
        </a-menu-item>
      </a-menu>
    </a-layout-header>
    <a-layout-content style="padding: 1.5em" v-bind:style="{minHeight: Height+'px'}"> 
      <div class="home" :loading="loading">
        <a-collapse :key="i" v-model="activeKey" v-for="(group,i) in list">
          <a-collapse-panel :key="i+''" :header="getgroup(group.Group,group.data.length)" >
            <a-row type="flex">
              <a-col class="gutter-row" span="6" :xs="24" :sm="12" :md="12" :lg="8" :xl="6" v-for="(item, index) in group.data" :key="index">
                <div class="gutter-box">
                  <a-card :title="getstate(item)" hoverable>
                    <a slot="extra" href="#">
                      <a-popover placement="leftTop">
                        <template slot="content">
                          <p>CPU: {{item.Cpu}}</p>
                          <p>内存: {{byteFormat(item.Status.MemUsed,2)}} / {{byteFormat(item.MemTotal,2)}}</p>
                          <p>硬盘:  {{byteFormat(item.Status.DiskUsed,1)}} / {{byteFormat(item.DiskTotal,1)}}</p>
                          <p>交换: {{byteFormat(item.Status.SwapUsed,2)}} / {{byteFormat(item.SwapTotal,2)}}</p>
                          <p>流量:<a-icon type="arrow-down" />{{byteFormat(item.Status.NetInTransfer)}} <a-icon type="arrow-up" />{{byteFormat(item.Status.NetOutTransfer)}}</p>
                          <p>负载: {{item.Status.Load1}} / {{item.Status.Load5}} / {{item.Status.Load15}}</p>
                        </template>
                        <a-icon type="more" />
                      </a-popover>
                    </a>
                      <div class="show-item">
                      <div class="item-label">系统</div>
                      <div class="item-content"> {{item.Platform}}[{{item.Arch}}]</div>
                    </div>
                   
                    <div class="show-item">
                      <div class="item-label">CPU</div>
                      <div class="item-content"><a-progress :strokeWidth=20 :percent="parseInt((item.Status.CpuUsed).toFixed())" :strokeColor="getProgressColor(parseInt((item.Status.CpuUsed).toFixed()))" /></div>
                    </div>
                    <div class="show-item">
                      <div class="item-label">内存</div>
                      <div class="item-content">
                        <a-progress :strokeWidth=20 :percent="parseInt((item.Status.MemUsed * 100 / item.MemTotal).toFixed())"  :strokeColor="getProgressColor(parseInt((item.Status.MemUsed * 100 / item.MemTotal).toFixed()))" />
                        </div>
                    </div>
                    <div class="show-item">
                      <div class="item-label">交换</div>
                      <div class="item-content"><a-progress :strokeWidth=20 :percent="parseInt((item.Status.SwapUsed * 100 / item.SwapTotal).toFixed())" :strokeColor="getProgressColor(parseInt((item.Status.SwapUsed * 100 / item.SwapTotal).toFixed()))" /></div>
                    </div>
                    <div class="show-item">
                      <div class="item-label">硬盘</div>
                      <div class="item-content"><a-progress :strokeWidth=20 :percent="parseInt((item.Status.DiskUsed * 100 / item.DiskTotal).toFixed())" :strokeColor="getProgressColor(parseInt((item.Status.DiskUsed * 100 / item.DiskTotal).toFixed()))" /></div>
                    </div>
                    <div class="show-item">
                      <div class="item-label">网络</div>
                      <div class="item-content">
                        <a-icon type="arrow-down" />{{byteFormat(item.Status.NetInSpeed)}}/s     <a-icon type="arrow-up" />{{byteFormat(item.Status.NetOutSpeed)}}/s
                      </div>
                    </div>
                    <div class="show-item">
                      <div class="item-label">在线</div>
                      <div class="item-content">{{(item.BootTime / (24 * 3600)).toFixed()+'天'}}</div>
                    </div>
                  </a-card>
                </div>
              </a-col>
            </a-row>
          </a-collapse-panel>
        </a-collapse>
      </div>
    </a-layout-content>
    <a-layout-footer style="text-align: center">
      ©2022   <small>Powered by <a href="https://github.com/zzzzpro/shandian"
              target="_blank">闪电监控</a></small>
    </a-layout-footer>
  </a-layout>
</template>

<script>
// @ is an alias to /src
const numeral = require('numeral')
const signalR = require("@microsoft/signalr");
import axios from 'axios'
numeral.nullFormat('0.00')
export default {
  name: 'Status',
  mounted () {
    this.logging = true
    this.Height = document.documentElement.clientHeight - 136;  
     window.onresize = ()=> {this.Height = document.documentElement.clientHeight -136}
    axios.get("/Server").then((res)=>{
       this.getdata(res.data);
    })
  },
  created(){
    this.init();
  },
  data() {
    return {
      loading: true,
      list: [],
      activeKey: ['0'],
      Height: 0
    }
  },
  methods: {
        getProgressColor (successPercent) {
        let color = ''
        if (successPercent > 70) {
          color = '#f50'
        } else if (successPercent >=30 && successPercent <= 70) {
          color = '#FF9900'
        } else if (successPercent < 30) {
          color = '#1890ff'
        }
        return color
    },
    getstate(obj){
      if(obj.State==1) {
        return obj.Remark;
      }else{
        return obj.Remark +'[已离线]';
      }
    },
    getgroup(obj,obj1){
      return obj+'('+obj1+')';
    },
    getdata(arr){
     var map = {},
      dest = [];
      for(var i = 0; i < arr.length; i++){
      var ai = arr[i];
      if(!map[ai.Group]){
        dest.push({
          Group: ai.Group,
          data: [ai]
        });
        map[ai.Group] = ai;
      }else{
        for(var j = 0; j < dest.length; j++){
          var dj = dest[j];
            if(dj.Group == ai.Group){
              dj.data.push(ai);
              break;
            }
        }
      }
      }
        this.list=dest;
    },
    getStatus(arr){
      for(var i=0;i<this.list.length;i++){
        for (let j = 0; j  < this.list[i].data.length; j ++) {
          for( let k=0;k<arr.length;k++){
            if(arr[k].Id==this.list[i].data[j].Id){
              this.list[i].data[j].Status=arr[k];
              this.list[i].data[j].State=arr[k].State;
              this.list[i].data[j].BootTime=arr[k].Uptime;
              break;
            }
          }
        }
      }
    },

    init() {
      var connection = new signalR.HubConnectionBuilder()
        .withUrl("/ws/server", {})
        //.withUrl("http://localhost:5000/api", {})
        .configureLogging(signalR.LogLevel.Error)
        .build();
        connection.on("status", data => {
        this.getStatus(JSON.parse(data));
        this.loading = false
      });
      connection.start().then(function() {
        }).catch(function() {
            setTimeout(() => start(), 5000);
        });

    function start() {
        try {
            connection.start();
        } catch (err) {
            console.log(err);
            setTimeout(() => start(), 5000);
        }
    }

    connection.onclose(() => {
        start();
    });
    },

    byteFormat (size,pos=0,show_init=true) {
    if (parseInt(size) === 0) {
        if(show_init) {
            return '0'
        }else{
            return '0'
        }
      
    }
    let unit = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']
    while (size >= 1000) {
      size /= 1000
      ++pos
    }
    return numeral(size).format('0.00') + (show_init?' '+unit[pos]:'')
  }
  }
}
</script>

<style >
.gutter-example >>> .ant-row > div {
  background: transparent;
  border: 0;
}
.ant-col {
  margin-bottom: 10px;
}
.gutter-box {
  /* background: #00a0e9; */
  padding: 5px;
}
.gutter-box .show-item {
  clear: both;
}
.gutter-box .show-item .item-label {
  width: 25%;
  float: left;
}
.gutter-box .show-item .item-content {
  width: 75%;
  float: left;
}
.ant-collapse{
  margin-bottom: 1.5em;
}
</style>
