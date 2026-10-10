package java.game;

import java.io.*;
import java.util.*;
import java.util.resource.*;
import java.net.*;

//SLRR-MP: sends the player's car state to the dinput8.dll bridge and mirrors remote players as ghost cars.
//The bridge interpolates; this only spawns, moves and removes ghosts (the owner's saved car once SlrrMp
//has transferred it, a stock car of the same model until then).
public class MpSync implements Runnable
{
	final static String OWN_CAR = "slrr-mp-cars/own.car"; //not under save/temp: the game wipes that folder

	Thread	thread;
	Track	track;
	GameRef	map;
	Vehicle	car;
	Vector	ghosts = new Vector();
	int	type, color, tickMs;

	public static void log(String s)
	{
		NetworkEngine.httpRequest("LOG", "", "", s);
	}

	public MpSync(Track track_, GameRef map_, Vehicle car_)
	{
		track = track_;
		map = map_;
		car = car_;
		String cfg = NetworkEngine.httpRequest("CFG", "", "", "");
		if(!cfg || !car) return; //no bridge installed, or no car (demo mode)
		tickMs = cfg.intValue();

		type = car.getInfo(GameType.GII_TYPE);
		color = -1;
		if(car.chassis)
		{
			int tex = car.chassis.getTexture();
			for(int i=0; i<GameLogic.CARCOLORS.length; i++) if(GameLogic.CARCOLORS[i] == tex) color = i;
		}
		log("enter city type=" + type + " color=" + color + " tick=" + tickMs);

		//exact look for the others: SlrrMp sends this file (+ its .1 .2 ... part files) to them
		for(int i=1; i<=64; i++) File.delete(OWN_CAR + "." + i); //no stale part files from a previous car
		car.save(OWN_CAR);
		NetworkEngine.httpRequest("CAR", "", "", OWN_CAR);

		thread = new Thread(this, "slrr-mp sync");
		thread.start();
	}

	//"t type color x y z yaw pitch roll vx vy vz flags", flags 1 = away.
	//t is the physics clock: it matches the sampled pose exactly (wall clock is off by up to a frame) and freezes on pause.
	public String state(int flags)
	{
		Vector3 p = car.getPos();
		Ypr o = car.getOri();
		Vector3 v = car.getVel();
		return System.simTime() + " " + type + " " + color + " " + p.x + " " + p.y + " " + p.z + " " + o.y + " " + o.p + " " + o.r + " " + v.x + " " + v.y + " " + v.z + " " + flags;
	}

	public void run()
	{
		int n = 0;
		for(;;)
		{
			String snap = NetworkEngine.httpRequest("MP", "", "", state(0));
			if(snap) apply(snap);
			if(++n % 30 == 0)
			{
				String chat = NetworkEngine.httpRequest("CHAT", "", "", "");
				if(chat) track.setMessage(chat, "yellow", 6);
			}
			Thread.sleep(tickMs);
		}
	}

	//"n id name type color car x y z yaw pitch roll ...", car = file path or "-"
	public void apply(String snap)
	{
		int i;
		MpGhost g;
		for(i=0; i<ghosts.size(); i++) { g = ghosts.elementAt(i); g.seen = 0; }

		int n = snap.token(0, " ").intValue();
		for(int k=0; k<n; k++)
		{
			int b = 1 + k*11;
			int id = snap.token(b, " ").intValue();
			String gname = snap.token(b+1, " ");
			int gtype = snap.token(b+2, " ").intValue();
			int gcolor = snap.token(b+3, " ").intValue();
			String gcar = snap.token(b+4, " ");

			MpGhost found = null;
			for(i=0; i<ghosts.size(); i++) { g = ghosts.elementAt(i); if(g.id == id) found = g; }
			g = found;
			if(g && (g.type != gtype || g.color != gcolor || g.car != gcar))
			{
				g.destroy();
				ghosts.removeElement(g);
				g = null; //found stays set: no second "is in the city"
			}
			if(!g)
			{
				if(!found) track.setMessage(gname + " is in the city", "yellow", 4);
				g = new MpGhost(track, map, id, gname, gtype, gcolor, gcar);
				ghosts.addElement(g);
				log("ghost " + id + " " + gname + " spawned type=" + gtype + " color=" + gcolor + " car=" + gcar + " loaded=" + g.loaded);
			}
			g.seen = 1;
			g.vhc.setMatrix(new Vector3(snap.token(b+5, " ").floatValue(), snap.token(b+6, " ").floatValue(), snap.token(b+7, " ").floatValue()),
				new Ypr(snap.token(b+8, " ").floatValue(), snap.token(b+9, " ").floatValue(), snap.token(b+10, " ").floatValue()));
		}

		for(i=ghosts.size()-1; i>=0; i--)
		{
			g = ghosts.elementAt(i);
			if(!g.seen)
			{
				log("ghost " + g.id + " removed");
				track.setMessage(g.name + " left the city", "yellow", 4);
				g.destroy();
				ghosts.removeElementAt(i);
			}
		}
	}

	public void stop()
	{
		log("exit city");
		if(!thread) return;
		thread.stop();
		log("sync thread stopped");
		NetworkEngine.httpRequest("MP", "", "", state(1));
		for(int i=0; i<ghosts.size(); i++) { MpGhost g = ghosts.elementAt(i); g.destroy(); }
		ghosts.removeAllElements();
		log("ghosts removed");
	}
}

public class MpGhost
{
	int	id, type, color, seen, loaded;
	String	car, name;
	Vehicle	vhc;
	Track	track;
	Marker	marker;

	public MpGhost(Track track_, GameRef map, int id_, String name_, int type_, int color_, String car_)
	{
		track = track_;
		name = name_;
		id = id_;
		type = type_;
		color = color_;
		car = car_;
		if(car != "-")
		{
			vhc = Vehicle.load(car, map);
			if(vhc && vhc.chassis)
			{
				vhc.setMatrix(null, null); //stop its physics like a stock spawn
				loaded = 1;
				addMarker();
				return;
			}
			if(vhc) vhc.destroy(); //parts this game doesn't have: fall back to stock
		}
		int c = color;
		if(c < 0 || c >= GameLogic.CARCOLORS.length) c = 0; //custom paint: closest is a stock color
		vhc = new Vehicle(map, type, c, 1.0, 1.0, 1.0, 1.0); //spawns with physics stopped
		addMarker();
	}

	//minimap dot, same symbol as race bots
	public void addMarker()
	{
		if(track.nav) marker = track.nav.addMarker(Marker.RR_V4_BOT, vhc);
	}

	public void destroy()
	{
		if(marker) track.nav.remMarker(marker);
		marker = null;
		if(vhc) vhc.destroy();
		vhc = null;
	}
}
