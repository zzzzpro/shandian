<template>
<div>
 <div class='search-head'>
        <div class="search-input">
          <a-input-search class="search-ipt" style="width: 522px" placeholder="描述，分类，或者ip..."  @search="search" size="large"  enter-button="搜索" v-model="key"/>
        </div>
      </div>
      <div class="search-content">
      <a-list
      :grid="{gutter: 16, xl: 4, lg: 3, md: 3, sm: 2, xs: 1}"
      style="margin: 0 -8px"
    >
      <a-list-item :key="item.id" v-for="(item) in list" style="padding: 0 8px">
        <a-card>
          <a-card-meta :title="item.remark">
          </a-card-meta>
            <a-tooltip class="tool"  title="编辑" slot="actions" @click="showModal(item)">
              <a-icon type="edit" />
            </a-tooltip>
            <a-tooltip class="tool"  title="删除" slot="actions" @click="deleteHost(item.id)">
              <a-icon type="delete" />
            </a-tooltip>
          <div class="content">
            <!-- <a-card-grid style="width:50%;text-align:left">
            ip:{{item.ip}}
            </a-card-grid>
            <a-card-grid style="width:50%;text-align:left">
            {{item.platform}}
            </a-card-grid> -->
            <p>系统：{{item.platform}}</p>
            <p>ip：  {{item.ip}}</p>
            <p>分类：{{item.group}}</p>
          </div>
        </a-card>
      </a-list-item>
    </a-list>
     <a-modal v-model="visible" title="编辑" @ok="handleOk">
    <a-form>
      <a-form-item
        label="描述"
        :labelCol="{span: 7}"
        :wrapperCol="{span: 10}"
      >
        <a-input placeholder="描述" v-model='model.remark' />
      </a-form-item>
     <a-form-item
        label="分类"
        :labelCol="{span: 7}"
        :wrapperCol="{span: 10}"
      >
        <a-input placeholder="分类" v-model='model.group' />
      </a-form-item>
    </a-form>
    </a-modal>
      </div>
  </div>

</template>

<script>
import {list,del,update} from '@/services/host'
export default {
  name: 'HostList',
  data() {
    return {
      loading: true,
      list: [],
      key:'',
      model:{},
      visible: false,
    }
  },
   created(){
    this.getData('');
  },
   methods:{
       search(){
         this.getData();
       },

       showModal(obj) {
        this.model=obj
        this.visible = true;
      },
      handleOk() {
         update(this.model).then((res)=>{
              if(res.data.code==0){
                 this.$message.success("更新成功", 3)
                 this.visible = false;
                 this.getData();
              }else{
                 this.$message.error("更新失败", 3)
              }
            });
        
      },
       getData(){
           list(this.key).then((res)=>{
               this.list=res.data.data
           })
       },
       updateHost(obj){
         this.showModal(obj);
          // this.$router.push('/dashboard/hostupdate?id='+obj)
       },
       deleteHost(obj){
           this.$confirm({
              title: '确定要删除吗',
              content: '删除后无法恢复',
              okText: '确定',
              okType: 'danger',
              cancelText: '取消',
              onOk:()=> {
                  del(obj).then((res)=>{
                    if(res.data.code==0){
                       this.$message.success("删除成功", 3)
                       this.getData();
                    }
                  })
                 
              },
              onCancel() {
                
            },
          });
         
       }
       
   }
   
}
//login(name, password).then(this.afterLogin)
</script>

<style lang="less" scoped>
.search-head{
    background-color: @base-bg-color;
    margin: 20px 0;
    &.head.fixed{
      margin: -24px 0;
    }
    .search-input{
      text-align: center;
    }
  }
  .search-content{
    margin-top: 0px;
  }
  .clearfix() {
    zoom: 1;
    &:before,
    &:after {
      content: ' ';
      display: table;
    }
    &:after {
      clear: both;
      visibility: hidden;
      font-size: 0;
      height: 0;
    }
  }
  .content {
    .clearfix();
    margin-top: 16px;
    & > div {
      position: relative;
      text-align: left;
      float: left;
    
      p {
        line-height: 32px;
        font-size: 24px;
        margin: 0;
        overflow: hidden;
        white-space: nowrap;
        text-overflow: ellipsis;
      }
      p:first-child {
        color: @text-color-second;
        font-size: 12px;
        line-height: 20px;
        margin-bottom: 4px;
      }
    }
  }
</style>
