import {HOSTS} from '@/services/api'
import {request, METHOD} from '@/utils/request'

export async function list(key) {
  return request(HOSTS, METHOD.GET, {
      key:key
  })
}
export async function update(host) {
    return request(HOSTS+'/update', METHOD.POST, host)
  }
  
export async function del(id) {
  return request(HOSTS+'/delete', METHOD.POST,{Id:id})
}
export async function detail(id) {
  console.log(id)
  return request(HOSTS+'/detail', METHOD.GET,{
    Id:id
})
}
export default {
    list,
    update,
    del
}
