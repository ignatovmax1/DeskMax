package su.familyserver.deskmax;
import org.junit.Test;
import static org.junit.Assert.*;
public class RemoteGeometryTest {
 @Test public void letterboxRejectsBarsAndNormalizesCenter(){assertNull(RemoteGeometry.point(50,10,100,100,200,100));assertArrayEquals(new double[]{0.5,0.5},RemoteGeometry.point(50,50,100,100,200,100),1e-9);}
 @Test public void edgesAndInvalidDimensions(){assertArrayEquals(new double[]{1,1},RemoteGeometry.point(100,75,100,100,200,100),1e-9);assertNull(RemoteGeometry.point(0,0,0,100,200,100));assertNull(RemoteGeometry.point(Float.NaN,0,100,100,100,100));}
}
