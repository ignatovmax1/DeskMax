package su.familyserver.deskmax;
public final class RemoteGeometry {
 public static double[] point(float x,float y,int width,int height,int imageWidth,int imageHeight){if(!Float.isFinite(x)||!Float.isFinite(y)||width<=0||height<=0||imageWidth<=0||imageHeight<=0)return null;double scale=Math.min((double)width/imageWidth,(double)height/imageHeight),w=imageWidth*scale,h=imageHeight*scale,left=(width-w)/2,top=(height-h)/2;if(x<left||y<top||x>left+w||y>top+h)return null;return new double[]{(x-left)/w,(y-top)/h};}
}
