# Build Vocaluxe (Linux): the two C/C++ native sub-projects (plain g++) and the .NET 10
# solution. Prerequisites are listed in HowToBuildLinux.md.

all:
	$(MAKE) -C PitchTracker
	$(MAKE) -C Vocaluxe/Lib/Video/Acinerella
	dotnet build Vocaluxe.slnx -c Release

clean:
	$(MAKE) -C PitchTracker clean
	$(MAKE) -C Vocaluxe/Lib/Video/Acinerella clean
	dotnet clean Vocaluxe.slnx -c Release

INSTALLPATH?=/usr/share/vocaluxe
install:
	mkdir -p $(DESTDIR)$(INSTALLPATH)
	cp -ru Output/. $(DESTDIR)$(INSTALLPATH)

ICONPATH?=/usr/share/applications
install_icon:
	mkdir -p $(DESTDIR)$(ICONPATH)
	cp Vocaluxe/Linux/Package/Vocaluxe.desktop $(DESTDIR)$(ICONPATH)/
	sed -i -e 's=INSTALLPATH=$(INSTALLPATH)=' $(DESTDIR)$(ICONPATH)/Vocaluxe.desktop
