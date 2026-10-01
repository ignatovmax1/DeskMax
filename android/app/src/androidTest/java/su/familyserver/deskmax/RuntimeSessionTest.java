package su.familyserver.deskmax;

import android.graphics.Bitmap;
import android.graphics.Color;
import android.graphics.drawable.ColorDrawable;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.TextView;
import androidx.test.core.app.ActivityScenario;
import androidx.test.ext.junit.runners.AndroidJUnit4;
import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import java.io.ByteArrayOutputStream;
import java.lang.reflect.Field;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import static org.junit.Assert.*;

/** Device tests use an owned synthetic host. They never capture the emulator's screen. */
@RunWith(AndroidJUnit4.class)
public final class RuntimeSessionTest {
 private interface Condition { boolean get() throws Exception; }
 private static void until(String description, Condition condition) throws Exception {
  long end=System.nanoTime()+TimeUnit.SECONDS.toNanos(70);
  while(System.nanoTime()<end){if(condition.get())return;Thread.sleep(150);}
  fail("Timed out: "+description);
 }
 private static Object field(Object object,String name){try{Field f=object.getClass().getDeclaredField(name);f.setAccessible(true);return f.get(object);}catch(Exception e){throw new AssertionError(e);}}
 private static View text(View view,String value){if(view instanceof TextView&&value.contentEquals(((TextView)view).getText()))return view;if(view instanceof ViewGroup){ViewGroup group=(ViewGroup)view;for(int i=0;i<group.getChildCount();i++){View found=text(group.getChildAt(i),value);if(found!=null)return found;}}return null;}
 private static View control(MainActivity activity,String tag,String caption){View root=activity.getWindow().getDecorView();View tagged=root.findViewWithTag(tag);return tagged!=null?tagged:text(root,caption);}
 @Test public void launchesDarkInterfaceWithConsentDisclosure(){
  try(ActivityScenario<MainActivity> scenario=ActivityScenario.launch(MainActivity.class)){
   scenario.onActivity(activity->{
    assertNotNull(control(activity,"connect","Подключиться"));
    assertNotNull(control(activity,"stop-session","Остановить сеанс"));
    assertNotNull(control(activity,"accessibility","Специальные возможности"));
    View content=(View)field(activity,"content");
    assertTrue(content.getBackground() instanceof ColorDrawable);
    int color=((ColorDrawable)content.getBackground()).getColor();
    assertTrue("Dark background",Color.red(color)<60&&Color.green(color)<60&&Color.blue(color)<60);
    View disclosure=control(activity,"permission-disclosure","Владелец подтверждает каждый сеанс и разрешает запись экрана. Для касаний включите службу DeskMax вручную в специальных возможностях. Защищённые экраны не передаются.");
    assertNotNull("Visible consent explanation",disclosure);
    assertEquals(View.VISIBLE,disclosure.getVisibility());
   });
  }
 }
 @Test public void receivesSyntheticJpegAndStopsRealRelaySession() throws Exception {
  DeskApi api=new DeskApi(DeskApi.DEFAULT_SERVER);
  JSONObject registered=api.post("api/devices",DeskApi.json("deviceName","Android instrumentation synthetic host","platform","Windows"));
  DeskApi.Identity host=new DeskApi.Identity(registered.getString("deviceId"),registered.getString("deviceSecret"));
  JSONObject hostSecret=DeskApi.json("deviceSecret",host.secret);
  String hostCode=api.post("api/devices/"+host.id+"/code",hostSecret).getString("code");
  SessionSocket hostSocket=null;String sessionId=null;
  CountDownLatch ready=new CountDownLatch(1),closed=new CountDownLatch(1);
  try(ActivityScenario<MainActivity> scenario=ActivityScenario.launch(MainActivity.class)){
   AtomicReference<DeskApi.Identity> viewer=new AtomicReference<>();
   until("viewer registration",()->{scenario.onActivity(a->viewer.set((DeskApi.Identity)field(a,"identity")));return viewer.get()!=null;});
   scenario.onActivity(a->{((EditText)field(a,"target")).setText(hostCode);assertTrue(control(a,"connect","Подключиться").performClick());});
   AtomicReference<String> request=new AtomicReference<>();
   until("owned host request",()->{JSONArray incoming=api.postArray("api/devices/"+host.id+"/sessions/incoming",hostSecret);for(int i=0;i<incoming.length();i++){JSONObject entry=incoming.getJSONObject(i);if(entry.optString("requesterDeviceId").equals(viewer.get().id)){request.set(entry.getString("sessionId"));return true;}}return false;});
   sessionId=request.get();api.post("api/sessions/"+sessionId+"/approve",hostSecret);
   hostSocket=new SessionSocket(DeskApi.DEFAULT_SERVER,host,sessionId,true,new SessionSocket.Listener(){public void onReady(){ready.countDown();}public void onFrame(byte[] frame){fail("Host must not receive a frame");}public void onInput(JSONObject input){}public void onClosed(String reason){closed.countDown();}});
   hostSocket.start();assertTrue("Both relay roles ready",ready.await(25,TimeUnit.SECONDS));
   Bitmap synthetic=Bitmap.createBitmap(48,32,Bitmap.Config.ARGB_8888);synthetic.eraseColor(Color.rgb(40,190,100));ByteArrayOutputStream bytes=new ByteArrayOutputStream();assertTrue(synthetic.compress(Bitmap.CompressFormat.JPEG,90,bytes));synthetic.recycle();hostSocket.sendFrame(bytes.toByteArray());
   AtomicReference<Bitmap> rendered=new AtomicReference<>();
   until("JPEG decoded into remote view",()->{scenario.onActivity(a->{Object remote=field(a,"remote");rendered.set(remote==null?null:(Bitmap)field(remote,"bitmap"));});return rendered.get()!=null;});
   scenario.onActivity(a->{Bitmap frame=(Bitmap)field(field(a,"remote"),"bitmap");assertEquals(48,frame.getWidth());assertEquals(32,frame.getHeight());int pixel=frame.getPixel(24,16);assertTrue(Color.green(pixel)>150);assertTrue(Color.red(pixel)<80);View remoteView=(View)field(a,"remote");assertTrue(remoteView.getWidth()>0&&remoteView.getHeight()>0);Bitmap display=Bitmap.createBitmap(remoteView.getWidth(),remoteView.getHeight(),Bitmap.Config.ARGB_8888);remoteView.draw(new android.graphics.Canvas(display));int displayed=display.getPixel(display.getWidth()/2,display.getHeight()/2);assertTrue("Synthetic frame is drawn",Color.green(displayed)>150);display.recycle();assertTrue(control(a,"stop-session","Остановить сеанс").performClick());assertNull(field(a,"remote"));});
   assertTrue("Host socket closes after viewer stop",closed.await(15,TimeUnit.SECONDS));
   JSONObject state=api.post("api/sessions/"+sessionId+"/status",DeskApi.json("deviceId",host.id,"deviceSecret",host.secret));assertEquals("ended",state.getString("status"));
  }finally{if(hostSocket!=null)hostSocket.close();if(sessionId!=null)try{api.post("api/sessions/"+sessionId+"/stop",DeskApi.json("deviceId",host.id,"deviceSecret",host.secret));}catch(Exception ignored){}}
 }
}


