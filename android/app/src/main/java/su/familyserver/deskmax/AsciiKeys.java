package su.familyserver.deskmax;
public final class AsciiKeys {
 public static int[] key(char c){if(c>='a'&&c<='z')return new int[]{c-'a'+65,0};if(c>='A'&&c<='Z')return new int[]{c,1};if(c>='0'&&c<='9')return new int[]{c,0};String plain=" ;=,-./`[\\]'",shift=" :+<_>?~{|}\"";int[] codes={32,186,187,188,189,190,191,192,219,220,221,222};int p=plain.indexOf(c);if(p>=0)return new int[]{codes[p],0};p=shift.indexOf(c);if(p>=0)return new int[]{codes[p],1};p=")!@#$%^&*(".indexOf(c);if(p>=0)return new int[]{48+p,1};if(c=='\n')return new int[]{13,0};return null;}
}
