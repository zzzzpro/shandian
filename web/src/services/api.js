//跨域代理前缀
// const API_PROXY_PREFIX='/api'
// const BASE_URL = process.env.NODE_ENV === 'production' ? process.env.VUE_APP_API_BASE_URL : API_PROXY_PREFIX
const BASE_URL = ''
module.exports = {
  LOGIN: `${BASE_URL}/Login/login`,
  ROUTES: `${BASE_URL}/routes`,
  HOSTS: `${BASE_URL}/Host`,
  USERS:`${BASE_URL}/User`
}
