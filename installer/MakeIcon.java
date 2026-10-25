import java.awt.Graphics2D;
import java.awt.Image;
import java.awt.image.BufferedImage;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.util.ArrayList;
import java.util.List;
import javax.imageio.ImageIO;

/**
 * build.sh : fabrique l'icône Windows de l'installateur (.ico) à partir de l'icône de l'appli, en toutes les tailles
 * qu'utilise Windows (16 à 256 px, réduites avec soin) : nette dans la barre de titre, la barre des tâches et
 * l'Explorateur, à toutes les échelles d'affichage.
 * Usage : java installer/MakeIcon.java app/res/drawable-nodpi/icon.png out/app.ico
 */
class MakeIcon {
    static final int[] SIZES = {16, 20, 24, 32, 40, 48, 64, 96, 128, 192, 256};

    public static void main(String[] args) throws Exception {
        BufferedImage source = ImageIO.read(new File(args[0]));
        List<byte[]> images = new ArrayList<>();
        for (int size : SIZES) {
            BufferedImage img = new BufferedImage(size, size, BufferedImage.TYPE_INT_ARGB);
            Graphics2D g = img.createGraphics();
            // Moyenne de zones : la meilleure réduction (pas d'escaliers ni de flou)
            g.drawImage(source.getScaledInstance(size, size, Image.SCALE_AREA_AVERAGING), 0, 0, null);
            g.dispose();
            images.add(size >= 96 ? png(img) : bmp(img));
        }
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        ByteBuffer head = ByteBuffer.allocate(6 + 16 * SIZES.length).order(ByteOrder.LITTLE_ENDIAN);
        head.putShort((short) 0).putShort((short) 1).putShort((short) SIZES.length);
        int offset = head.capacity();
        for (int i = 0; i < SIZES.length; i++) {
            int s = SIZES[i] >= 256 ? 0 : SIZES[i];
            head.put((byte) s).put((byte) s).put((byte) 0).put((byte) 0);
            head.putShort((short) 1).putShort((short) 32).putInt(images.get(i).length).putInt(offset);
            offset += images.get(i).length;
        }
        out.write(head.array());
        for (byte[] b : images) out.write(b);
        try (FileOutputStream f = new FileOutputStream(args[1])) { out.writeTo(f); }
    }

    static byte[] png(BufferedImage img) throws Exception {
        ByteArrayOutputStream b = new ByteArrayOutputStream();
        ImageIO.write(img, "png", b);
        return b.toByteArray();
    }

    /** Petites tailles en bitmap 32 bits (le format que lisent tous les Windows), avec son masque vide. */
    static byte[] bmp(BufferedImage img) {
        int w = img.getWidth(), h = img.getHeight(), mask = ((w + 31) / 32) * 4 * h;
        ByteBuffer b = ByteBuffer.allocate(40 + w * h * 4 + mask).order(ByteOrder.LITTLE_ENDIAN);
        b.putInt(40).putInt(w).putInt(h * 2).putShort((short) 1).putShort((short) 32).putInt(0)
                .putInt(w * h * 4 + mask).putInt(0).putInt(0).putInt(0).putInt(0);
        for (int y = h - 1; y >= 0; y--) {   // de bas en haut
            for (int x = 0; x < w; x++) b.putInt(img.getRGB(x, y));   // ARGB en petit-boutiste = B, G, R, A
        }
        return b.array();   // le masque reste à zéro
    }
}
