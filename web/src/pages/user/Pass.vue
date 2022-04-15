<template>
    <a-form>
       <a-form-item
        label="原用户名"
        :labelCol="{span: 7}"
        :wrapperCol="{span: 10}"
      >
        <a-input placeholder="原用户名" v-model='model.OldUserName' />
      </a-form-item>
         <a-form-item
        label="原密码"
        :labelCol="{span: 7}"
        :wrapperCol="{span: 10}"
      >
        <a-input placeholder="原密码" v-model='model.OldPassword' />
      </a-form-item>
        <a-form-item
        label="新用户名"
        :labelCol="{span: 7}"
        :wrapperCol="{span: 10}"
      >
        <a-input placeholder="新用户名" v-model='model.NewUserName' />
      </a-form-item>
         <a-form-item
        label="新密码"
        :labelCol="{span: 7}"
        :wrapperCol="{span: 10}"
      >
        <a-input placeholder="新密码" v-model='model.NewPassword' />
      </a-form-item>
      <a-form-item style="margin-top: 24px" :wrapperCol="{span: 10, offset: 7}">
        <a-button type="primary" @click="UpadateUser">修改</a-button>
      </a-form-item>
    </a-form>
</template>

<script>
import {update} from '@/services/user'
export default {
  name: 'Pass',
  data () {
    return {
      model: {}
    }
  },
  methods:{
      UpadateUser(){
           update(this.model).then((res)=>{
              if(res.data.code==0){
                 this.$message.success("更新成功", 3)
                 this.visible = false;
                 this.getData();
              }else{
                 this.$message.error("更新失败", 3)
              }
            });
        
    }
  }
   
}
 

</script>

<style scoped>

</style>
