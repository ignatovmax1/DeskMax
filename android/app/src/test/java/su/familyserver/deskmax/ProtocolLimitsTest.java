package su.familyserver.deskmax;
import org.junit.Test;import static org.junit.Assert.*;
public class ProtocolLimitsTest {
 @Test public void updateRejectsDowngradesPreviewsAndMalformedVersions(){assertTrue(ProtocolLimits.newerRelease("v0.10.0","0.4.0"));assertFalse(ProtocolLimits.newerRelease("v0.3.0","0.4.0"));assertFalse(ProtocolLimits.newerRelease("v0.4.0","0.4.0"));assertFalse(ProtocolLimits.newerRelease("v0.5.0-beta","0.4.0"));assertFalse(ProtocolLimits.newerRelease("v999999999999.0.0","0.4.0"));}
 @Test public void secureServerRequiresHostAndNoCredentials(){assertTrue(ProtocolLimits.validServer("https://anydesk.familyserver.su/"));assertFalse(ProtocolLimits.validServer("http://host/"));assertFalse(ProtocolLimits.validServer("https://user:password@host/"));assertFalse(ProtocolLimits.validServer("https://host/?token=secret"));}
 @Test public void utf8LimitCountsBytes(){assertTrue(ProtocolLimits.validMessage("a".repeat(4096)));assertFalse(ProtocolLimits.validMessage("\u044f".repeat(2049)));}
 @Test public void textRejectsBrokenUnicode(){assertTrue(ProtocolLimits.validText("\u041f\u0440\u0438\u0432\u0435\u0442 \ud83d\ude00"));assertFalse(ProtocolLimits.validText("\ud83d"));assertFalse(ProtocolLimits.validText("\u0000"));assertFalse(ProtocolLimits.validText("a".repeat(1025)));}
}
