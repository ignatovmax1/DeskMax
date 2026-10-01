package su.familyserver.deskmax;
import java.net.URI;
import java.nio.charset.StandardCharsets;
public final class ProtocolLimits {
 private ProtocolLimits(){}
 public static boolean newerRelease(String tag,String current){
  if(tag==null||current==null||!tag.matches("v[0-9]+\\.[0-9]+\\.[0-9]+")||!current.matches("[0-9]+\\.[0-9]+\\.[0-9]+"))return false;
  String[] next=tag.substring(1).split("\\."), installed=current.split("\\.");
  try{for(int i=0;i<3;i++){int a=Integer.parseInt(next[i]),b=Integer.parseInt(installed[i]);if(a!=b)return a>b;}}catch(NumberFormatException ignored){return false;}return false;
 }
 public static boolean validServer(String base){try{URI uri=URI.create(base);return "https".equals(uri.getScheme())&&uri.getHost()!=null&&uri.getUserInfo()==null&&uri.getQuery()==null&&uri.getFragment()==null;}catch(IllegalArgumentException e){return false;}}
 public static boolean validText(String text){if(text==null||text.isEmpty()||text.length()>1024)return false;for(int i=0;i<text.length();i++){char c=text.charAt(i);if(Character.isHighSurrogate(c)){if(++i>=text.length()||!Character.isLowSurrogate(text.charAt(i)))return false;}else if(Character.isLowSurrogate(c)||c==0)return false;}return true;}
 public static boolean validMessage(String text){return text!=null&&text.getBytes(StandardCharsets.UTF_8).length<=4096;}
}
