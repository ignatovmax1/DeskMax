package su.familyserver.deskmax;

import java.io.IOException;
import java.util.concurrent.TimeUnit;
import okhttp3.*;
import org.json.*;

public final class DeskApi {
 public static final String DEFAULT_SERVER="https://anydesk.familyserver.su/";
 public static final OkHttpClient HTTP=new OkHttpClient.Builder().connectTimeout(15,TimeUnit.SECONDS).readTimeout(20,TimeUnit.SECONDS).callTimeout(25,TimeUnit.SECONDS).build();
 public static final class Identity { public final String id,secret; public Identity(String id,String secret){this.id=id;this.secret=secret;} }
 private final String base;
 public DeskApi(String base){if(!ProtocolLimits.validServer(base))throw new IllegalArgumentException("HTTPS required");this.base=base.endsWith("/")?base:base+"/";}
 public static JSONObject json(Object... pairs){JSONObject o=new JSONObject();try{for(int i=0;i<pairs.length;i+=2)o.put((String)pairs[i],pairs[i+1]);}catch(JSONException e){throw new IllegalArgumentException(e);}return o;}
 private String request(String path,JSONObject body)throws Exception{
  Request request=new Request.Builder().url(base+path.replaceFirst("^/","")).post(RequestBody.create(body.toString(),MediaType.get("application/json"))).build();
  try(Response r=HTTP.newCall(request).execute()){if(!r.isSuccessful())throw new IOException("Сервер ответил "+r.code());if(r.body()==null)return "{}";String value=r.body().string();if(value.length()>262144)throw new IOException("Ответ слишком большой");return value.isEmpty()?"{}":value;}
 }
 public JSONObject post(String path,JSONObject body)throws Exception{return new JSONObject(request(path,body));}
 public JSONArray postArray(String path,JSONObject body)throws Exception{return new JSONArray(request(path,body));}
}
