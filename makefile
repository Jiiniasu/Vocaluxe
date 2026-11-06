# Build Vocaluxe (Linux): the C++ native sub-project (PitchTracker, plain g++) and the .NET 10
# solution. Prerequisites are listed in HowToBuildLinux.md. FFmpeg is a build-time download
# (see the _StageFFmpegLinux target in Vocaluxe/Vocaluxe.csproj) - no system FFmpeg needed.

all:
	$(MAKE) -C PitchTracker
	dotnet build Vocaluxe.slnx -c Release

clean:
	$(MAKE) -C PitchTracker clean
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
