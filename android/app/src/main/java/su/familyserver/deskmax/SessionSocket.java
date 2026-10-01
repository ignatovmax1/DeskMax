package su.familyserver.deskmax;

import java.util.concurrent.*;
import java.util.concurrent.atomic.AtomicBoolean;
import okhttp3.*;
import okio.ByteString;
import org.json.*;

public final class SessionSocket extends WebSocketListener {
 public interface Listener {void onReady();void onFrame(byte[] frame);void onInput(JSONObject input);void onClosed(String reason);}
 private final String base,id;private final DeskApi.Identity identity;private final boolean host;private final Listener listener;
 private final AtomicBoolean closed=new AtomicBoolean();private volatile boolean role,ready;private WebSocket socket;
 private final ScheduledExecutorService timer=Executors.newSingleThreadScheduledExecutor();private volatile long last=System.currentTimeMillis();
 public SessionSocket(String base,DeskApi.Identity identity,String id,boolean host,Listener listener){this.base=base;this.identity=identity;this.id=id;this.host=host;this.listener=listener;}
 public void start(){String url=(base.endsWith("/")?base:base+"/")+"api/sessions/"+id+"/transport";if(!ProtocolLimits.validServer(base)){finish("HTTPS required");return;}
  socket=DeskApi.HTTP.newBuilder().callTimeout(0,TimeUnit.SECONDS).pingInterval(20,TimeUnit.SECONDS).build().newWebSocket(new Request.Builder().url(url.replaceFirst("https:","wss:")).build(),this);
  timer.scheduleWithFixedDelay(()->{if(System.currentTimeMillis()-last>20000)finish("Соединение потеряно");},5,5,TimeUnit.SECONDS);
  timer.schedule(()->{if(!ready)finish("Время ожидания сеанса истекло");},60,TimeUnit.SECONDS);
 }
 @Override public void onOpen(WebSocket s,Response r){if(closed.get()){s.cancel();return;}s.send(DeskApi.json("deviceId",identity.id,"deviceSecret",identity.secret).toString());}
 @Override public void onMessage(WebSocket s,String text){last=System.currentTimeMillis();try{if(!ProtocolLimits.validMessage(text))throw new JSONException("limit");JSONObject o=new JSONObject(text);String type=o.optString("type");
  if(!role){if(!type.equals("role")||!o.optString("role").equals(host?"host":"viewer"))throw new JSONException("role");role=true;return;}
  if(type.equals("ready")){if(!ready){ready=true;listener.onReady();}}else if(type.equals("heartbeat")){}else if(host&&ready&&type.isEmpty())listener.onInput(o);else throw new JSONException("unexpected");
 }catch(Exception e){finish("Неверное сообщение сеанса");}}
 @Override public void onMessage(WebSocket s,ByteString bytes){last=System.currentTimeMillis();if(host||!ready||bytes.size()>2097152||bytes.size()<4){finish("Неверный видеокадр");return;}listener.onFrame(bytes.toByteArray());}
 public void sendFrame(byte[] frame){if(!host||!ready||closed.get())return;if(frame.length>2097152){finish("Кадр слишком большой");return;}if(socket.queueSize()>2097152)return;if(!socket.send(ByteString.of(frame)))finish("Передача остановлена");}
 public void sendInput(JSONObject input){if(host||!ready||closed.get())return;String text=input.toString();if(!ProtocolLimits.validMessage(text)||socket.queueSize()>65536||!socket.send(text))finish("Очередь ввода переполнена");}
 @Override public void onClosed(WebSocket s,int code,String reason){finish("Сеанс завершён");}
 @Override public void onClosing(WebSocket s,int code,String reason){s.close(code,reason);finish("Сеанс завершён");}
 @Override public void onFailure(WebSocket s,Throwable t,Response response){finish("Соединение потеряно");}
 private void finish(String reason){if(closed.compareAndSet(false,true)){ready=false;timer.shutdownNow();if(socket!=null)socket.cancel();listener.onClosed(reason);}}
 public void close(){finish("Сеанс завершён");}
 public void stop(){close();Request r=new Request.Builder().url(base+(base.endsWith("/")?"":"/")+"api/sessions/"+id+"/stop").post(RequestBody.create(DeskApi.json("deviceId",identity.id,"deviceSecret",identity.secret).toString(),MediaType.get("application/json"))).build();DeskApi.HTTP.newCall(r).enqueue(new Callback(){public void onFailure(Call c,java.io.IOException e){}public void onResponse(Call c,Response r){r.close();}});}
}
